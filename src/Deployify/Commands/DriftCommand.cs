using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands.Json;
using Deployify.Core.Deployments;
using Deployify.Core.Drift;
using Deployify.Core.Interfaces;
using Deployify.Core.Services;
using Deployify.Core.State;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Checks whether environments still match what was last deployed to them, and alerts when they
/// don't. Meant to run on a schedule (a CI cron job, a Kubernetes CronJob, or <c>--every</c>).
/// </summary>
/// <remarks>Exit codes: 0 nothing drifted, 2 something drifted, 1 a check or an alert failed.</remarks>
public static partial class DriftCommand
{
    /// <summary>The exit code when at least one environment drifted.</summary>
    public const int DriftedExitCode = 2;

    /// <summary>Shortest <c>--every</c> interval: each check asks the target for its whole state.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"deployify/{StateFormat.ToolVersion.Replace(' ', '-')}");
        return client;
    });

    /// <summary>What sends the alerts. Tests replace it.</summary>
    internal static Func<IDriftNotifier> NotifierFactory { get; set; } = () => new HttpDriftNotifier(s_http.Value);

    public static Command Build()
    {
        var all = new Option<bool>("--all")
        {
            Description = "Check every environment in deployify-state.json (except abstract ones and ones with " +
                          "drift.enabled: false) instead of only the active one or --env."
        };
        var noNotify = new Option<bool>("--no-notify")
        {
            Description = "Report only: send no alerts and leave the recorded result of the last check alone, " +
                          "so a check by hand doesn't use up the scheduled job's alerts."
        };
        var every = new Option<TimeSpan?>("--every")
        {
            Description = "Keep running and check again at this interval (for example 15m, 1h, 1d) until stopped. " +
                          "For a long-running container; in CI, schedule the job instead.",
            CustomParser = r => ParseInterval(r.Tokens.Single().Value, out var interval, out var error)
                ? interval
                : Fail(r, error)
        };
        var summary = new Option<bool>("--summary")
        {
            Description = "Print only which services drifted, without the live and deployed values."
        };

        var cmd = new Command("drift",
            "Check whether environments still match what was last deployed, and send alerts (drift.notify) when they don't.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(all);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(noNotify);
        cmd.Options.Add(every);
        cmd.Options.Add(summary);
        cmd.Options.Add(GlobalOptions.Output);

        cmd.Validators.Add(r =>
        {
            if (r.GetValue(all) && r.GetValue(GlobalOptions.Environment) is not null)
            {
                r.AddError("--all checks every environment; leave out --env, or leave out --all to check one.");
            }

            // Its tokens, not its value: reading the value of an --every that didn't parse throws.
            if (r.GetResult(every) is { Tokens.Count: > 0 } && r.GetValue(GlobalOptions.Output) == OutputFormat.Json)
            {
                r.AddError("--every keeps running, but --output json writes one document when the command ends. Use one or the other.");
            }

            if (r.GetValue(all) && r.GetValue(GlobalOptions.Target) is not null)
            {
                r.AddError("--target would apply to every environment; leave it out with --all.");
            }
        });

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "drift", async json =>
        {
            var options = new Options(
                parseResult.GetValue(GlobalOptions.ProjectPath)!,
                parseResult.GetValue(GlobalOptions.Environment),
                parseResult.GetValue(all),
                parseResult.GetValue(GlobalOptions.Target),
                parseResult.GetValue(GlobalOptions.AllowUnverified),
                parseResult.GetValue(noNotify),
                parseResult.GetValue(summary));

            if (parseResult.GetValue(every) is not { } interval)
            {
                return await RunOnceAsync(options, json, ct);
            }

            AnsiConsole.MarkupLineInterpolated($"[dim]Checking every {Describe(interval)}. Press Ctrl+C to stop.[/]");
            try
            {
                while (true)
                {
                    await RunOnceAsync(options, json, ct);
                    AnsiConsole.MarkupLineInterpolated($"[dim]Next check at {DateTimeOffset.Now.Add(interval):g}.[/]\n");
                    await Task.Delay(interval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return 0;
            }
        }));

        return cmd;
    }

    private sealed record Options(string ProjectPath, string? Environment, bool All, Core.Models.DeploymentTargetType? Target,
        bool AllowUnverified, bool NoNotify, bool Summary);

    private static async Task<int> RunOnceAsync(Options options, JsonRun? json, CancellationToken ct)
    {
        IReadOnlyList<string?> environments;
        if (options.All)
        {
            try
            {
                environments = await ListEnvironmentsAsync(options.ProjectPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }
        }
        else
        {
            environments = [options.Environment];
        }

        var notifier = options.NoNotify ? null : NotifierFactory();
        var runs = new List<DriftRun>();
        foreach (var env in environments)
        {
            var run = await CheckOneAsync(options, env, notifier, json, single: !options.All, ct);
            if (run is not null)
            {
                runs.Add(run);
                Print(run, options.Summary);
            }
        }

        if (runs.Count == 0)
        {
            if (options.All)
            {
                ConsoleDeploymentReporter.Instance.Error("deployify-state.json has no environment to check.");
            }

            return 1;
        }

        if (runs.Count > 1)
        {
            AnsiConsole.MarkupLine("[bold]Drift:[/] " + string.Join(", ",
                runs.Select(r => $"{Markup.Escape(r.Check.Environment)} {StatusMarkup(r.Check.Status)}")));
        }

        if (json is not null)
        {
            json.Result = ToJson(runs);
        }

        return ExitCode(runs);
    }

    /// <summary>0 when nothing drifted and every check and alert worked, 2 when something drifted, otherwise 1.</summary>
    internal static int ExitCode(IReadOnlyCollection<DriftRun> runs)
    {
        if (runs.Any(r => r.Check.Status == DriftStatus.Drifted))
        {
            return DriftedExitCode;
        }

        var alertsFailed = runs.Any(r => r.Deliveries.Count > 0 && r.Deliveries.All(d => !d.Ok));
        return runs.Any(r => r.Check.Status is DriftStatus.Error or DriftStatus.Unknown) || alertsFailed ? 1 : 0;
    }

    /// <summary>
    /// Resolves and checks one environment. Returns null only when it couldn't even be resolved
    /// and there is no name to report it under.
    /// </summary>
    private static async Task<DriftRun?> CheckOneAsync(Options options, string? env, IDriftNotifier? notifier, JsonRun? json,
        bool single, CancellationToken ct)
    {
        ResolvedContext resolved;
        try
        {
            resolved = await DependencyInjection.ResolveAsync(options.ProjectPath, env, options.Target, ct,
                allowUnverifiedPlugins: options.AllowUnverified);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleDeploymentReporter.Instance.Error(env is null ? ex.Message : $"{env}: {ex.Message}");
            return env is null ? null : new DriftRun(new DriftCheck
            {
                Environment = env,
                Status = DriftStatus.Error,
                CheckedAt = DateTimeOffset.UtcNow,
                Reason = ex.Message
            }, null, [], null);
        }

        await using var _ = resolved;
        if (single)
        {
            json?.Use(resolved);
        }

        if (options.All && resolved.Config.Drift is { Enabled: false })
        {
            return new DriftRun(new DriftCheck
            {
                Environment = resolved.EnvName,
                Status = DriftStatus.Skipped,
                CheckedAt = DateTimeOffset.UtcNow,
                TargetId = resolved.TargetType.Value,
                TargetName = resolved.Target.DisplayName,
                Reason = "Drift checks are off for this environment (drift.enabled: false)."
            }, null, [], null);
        }

        var run = await DriftMonitor.RunAsync(resolved, notifier, ct: ct);
        foreach (var failed in run.Deliveries.Where(d => !d.Ok))
        {
            ConsoleDeploymentReporter.Instance.Error($"{resolved.EnvName}: the alert to {failed.Channel} failed: {failed.Error}");
        }

        // Recorded for --output json only (the text output prints them with the check), so the
        // document's error says why the exit code isn't 0.
        switch (run.Check.Status)
        {
            case DriftStatus.Drifted:
                JsonOutput.Current?.Report(JsonSeverity.Error,
                    $"{run.Check.Environment} drifted from its last deploy: {DriftMessages.Counts(run.Check)}.");
                break;
            case DriftStatus.Error:
                JsonOutput.Current?.Report(JsonSeverity.Error, $"{run.Check.Environment}: {run.Check.Reason}");
                break;
            case DriftStatus.Unknown:
                JsonOutput.Current?.Report(JsonSeverity.Error,
                    $"{run.Check.Environment}: {run.Check.Count(DriftKind.Unknown)} service(s) couldn't be compared.");
                break;
        }

        return run;
    }

    /// <summary>The deployable environments, in the order deployify-state.json lists them.</summary>
    private static async Task<IReadOnlyList<string?>> ListEnvironmentsAsync(string projectPath, CancellationToken ct)
    {
        var start = File.Exists(projectPath) ? Path.GetDirectoryName(Path.GetFullPath(projectPath))! : Path.GetFullPath(projectPath);
        var directory = DeploymentSessionResolver.FindStateDirectory(start)
                        ?? throw new FileNotFoundException(
                            $"{DeploymentSessionResolver.StateFileName} not found in '{start}' or any parent directory. Run 'deployify init' first.");
        var file = await new StateManager().LoadFileAsync(directory, ct);
        return [.. file.Environments.Where(e => !e.Value.Abstract).Select(e => (string?)e.Key)];
    }

    // ── Text output ───────────────────────────────────────────────────────────

    private static void Print(DriftRun run, bool summary)
    {
        var check = run.Check;
        var target = check.TargetName is null ? "" : $" [dim]({Markup.Escape(check.TargetName)})[/]";
        AnsiConsole.MarkupLine($"[bold cyan]{Markup.Escape(check.Environment)}[/]{target}: {StatusMarkup(check.Status)}");

        if (check.Baseline is { } b)
        {
            var details = string.Join(", ", new[] { b.Tag is null ? null : $"tag {b.Tag}", b.Commit is null ? null : $"commit {b.Commit[..Math.Min(12, b.Commit.Length)]}", b.Actor is null ? null : $"by {b.Actor}" }
                .Where(s => s is not null));
            AnsiConsole.MarkupLineInterpolated($"[dim]  Compared with the deploy of {b.At.ToLocalTime():g}{(details.Length == 0 ? "" : $" ({details})")}.[/]");
        }

        if (check.Reason is not null)
        {
            var color = check.Status == DriftStatus.Error ? "red" : "dim";
            AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(check.Reason)}[/]");
        }

        if (check.Entries.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Service").AddColumn("Drift");
            foreach (var e in check.Entries)
            {
                var (icon, color, label) = e.Kind switch
                {
                    DriftKind.Missing => ("-", "red", "missing: deployed, no longer on the target"),
                    DriftKind.Extra => ("+", "yellow", "extra: on the target, not deployed"),
                    DriftKind.Changed => ("~", "yellow", e.Field is null ? "changed" : $"changed ({e.Field})"),
                    _ => ("?", "blue", $"not checked{(e.Reason is null ? "" : $" ({e.Reason})")}")
                };
                table.AddRow($"[{color}]{icon} {Markup.Escape(e.Service)}[/]", $"[{color}]{Markup.Escape(label)}[/]");
            }

            AnsiConsole.Write(table);
            if (!summary)
            {
                PrintValues(check.Entries);
            }
        }

        if (check.Ignored > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]  {check.Ignored} expected difference(s) ignored (drift.ignore).[/]");
        }

        if (run.Event is { } alert)
        {
            if (run.Deliveries.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]  Alert ({JsonOutput.Name(alert)}) not sent: no channels in drift.notify.[/]");
            }
            else
            {
                var sent = string.Join(", ", run.Deliveries.Select(d => d.Ok
                    ? $"[green]✓[/] {Markup.Escape(d.Channel)}"
                    : $"[red]✗[/] {Markup.Escape(d.Channel)}"));
                AnsiConsole.MarkupLine($"  Alert ([bold]{JsonOutput.Name(alert)}[/]): {sent}");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintValues(IEnumerable<DriftEntry> entries)
    {
        foreach (var e in entries.Where(e => e.Kind == DriftKind.Changed && (e.Live is not null || e.Deployed is not null)))
        {
            var heading = $"  [bold]{Markup.Escape(e.Service)}[/]" + (e.Field is null ? "" : $" [dim]{Markup.Escape(e.Field)}[/]");
            var multiline = (e.Live?.Contains('\n') ?? false) || (e.Deployed?.Contains('\n') ?? false);
            if (multiline)
            {
                // Kubernetes and Helm: one rendered diff; '-' lines are live, '+' lines what was deployed.
                AnsiConsole.MarkupLine(heading + " [dim](- live, + deployed)[/]");
                foreach (var text in new[] { e.Live, e.Deployed }.Where(t => t is not null))
                {
                    foreach (var line in text!.Replace("\r\n", "\n").Split('\n'))
                    {
                        var color = line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) ? "green"
                            : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal) ? "red" : "dim";
                        AnsiConsole.MarkupLine($"    [{color}]{Markup.Escape(line)}[/]");
                    }
                }

                continue;
            }

            AnsiConsole.MarkupLine($"{heading}: live [red]{Markup.Escape(e.Live ?? "(none)")}[/], deployed [green]{Markup.Escape(e.Deployed ?? "(none)")}[/]");
        }
    }

    private static string StatusMarkup(DriftStatus status) => status switch
    {
        DriftStatus.InSync => "[green]in sync[/]",
        DriftStatus.Drifted => "[red]drifted[/]",
        DriftStatus.Unknown => "[blue]partly checked[/]",
        DriftStatus.Error => "[red]check failed[/]",
        DriftStatus.NotDeployed => "[dim]not deployed yet[/]",
        _ => "[dim]skipped[/]"
    };

    // ── JSON output ───────────────────────────────────────────────────────────

    internal static JsonDrift ToJson(IReadOnlyCollection<DriftRun> runs) => new(
        runs.Any(r => r.Check.Status == DriftStatus.Drifted),
        [.. runs.Select(r =>
        {
            var c = r.Check;
            var b = c.Baseline;
            return new JsonDriftEnvironment(
                c.Environment,
                c.TargetId is null ? null : new JsonTarget(c.TargetId, c.TargetName ?? c.TargetId),
                JsonOutput.Name(c.Status),
                c.CheckedAt,
                c.Reason,
                b is null ? null : new JsonDriftDeployed(b.ReleaseId, b.At, b.Tag, b.Commit, b.Actor, b.RunUrl),
                [.. c.Entries.Select(e => new JsonDriftEntry(e.Service, JsonOutput.Name(e.Kind), e.Field, e.Live, e.Deployed, e.Reason))],
                c.Ignored,
                c.Fingerprint,
                r.Event is { } ev
                    ? new JsonDriftAlert(JsonOutput.Name(ev),
                        [.. r.Deliveries.Select(d => new JsonDriftDelivery(d.Channel, JsonOutput.Name(d.Type), d.Ok, d.Error))])
                    : null);
        })]);

    // ── --every ───────────────────────────────────────────────────────────────

    /// <summary>Reads <c>90s</c>, <c>15m</c>, <c>1h</c>, <c>1d</c> or <c>hh:mm:ss</c>; at least <see cref="MinimumInterval"/>.</summary>
    internal static bool ParseInterval(string text, out TimeSpan interval, out string error)
    {
        error = "";
        var match = IntervalPattern().Match(text.Trim());
        if (match.Success)
        {
            var n = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            interval = match.Groups[2].Value.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                _ => TimeSpan.FromDays(n)
            };
        }
        else if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out interval))
        {
            error = $"'{text}' is not an interval. Use for example 15m, 1h or 1d.";
            return false;
        }

        if (interval < MinimumInterval)
        {
            error = $"--every must be at least {Describe(MinimumInterval)}: each check asks the target for its whole state.";
            return false;
        }

        return true;
    }

    private static TimeSpan? Fail(System.CommandLine.Parsing.ArgumentResult result, string error)
    {
        result.AddError(error);
        return null;
    }

    private static string Describe(TimeSpan t)
        => t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0} day(s)"
            : t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} hour(s)"
            : t.TotalMinutes % 1 == 0 ? $"{t.TotalMinutes:0} minute(s)"
            : t.ToString();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([smhd])$", RegexOptions.IgnoreCase)]
    private static partial Regex IntervalPattern();
}
