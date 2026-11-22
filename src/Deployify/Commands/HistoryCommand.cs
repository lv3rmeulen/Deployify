using System;
using System.CommandLine;
using System.Linq;
using Deployify.Commands.Json;
using Deployify.Core.Models;
using Deployify.Core.State;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Lists the environment's release history: who published, promoted, deployed or rolled back
/// which images, from which commit, and when (see <see cref="ReleaseLog"/>).
/// </summary>
public static class HistoryCommand
{
    public static Command Build()
    {
        var limit = new Option<int>("--limit")
        {
            Description = "How many releases to list, newest first (default: 20).",
            DefaultValueFactory = _ => 20
        };

        var cmd = new Command("history", "Show who published, promoted and deployed which images, and when.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(limit);
        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "history", async json =>
        {
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(
                    parseResult.GetValue(GlobalOptions.ProjectPath)!,
                    parseResult.GetValue(GlobalOptions.Environment),
                    parseResult.GetValue(GlobalOptions.Target), ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            await using var _ = resolved;
            json?.Use(resolved);

            System.Collections.Generic.IReadOnlyList<ReleaseRecord> releases;
            try
            {
                releases = await ReleaseLog.ListAsync(resolved.State, Math.Max(1, parseResult.GetValue(limit)), ct);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or StateFormatException))
            {
                ConsoleDeploymentReporter.Instance.Error(
                    $"Could not read the release history of {resolved.EnvName} ({resolved.State.Backend.Description}): {ex.Message}");
                return 1;
            }

            if (json is not null)
            {
                json.Result = new JsonHistory([.. releases.Select(ToJson)]);
            }

            var disabled = Core.Deployments.ReleaseRecorder.IsDisabled(resolved.State);
            if (disabled)
            {
                ConsoleDeploymentReporter.Instance.Warn(
                    $"Release history is off for {resolved.EnvName} (state.history.keep is -1): new releases aren't recorded." +
                    (releases.Count > 0 ? " These were recorded before it was turned off." : ""));
            }

            if (releases.Count == 0)
            {
                if (disabled)
                {
                    return 0;
                }

                AnsiConsole.MarkupLineInterpolated($"No releases recorded for [cyan]{resolved.EnvName}[/].");
                if (!resolved.State.IsRemote)
                {
                    AnsiConsole.MarkupLine("[dim]History is kept in this machine's local state. Configure remote state " +
                                           "to share it with CI and your team.[/]");
                }

                return 0;
            }

            var table = new Table().AddColumns("When", "Release", "Outcome", "Images", "Commit", "By", "Detail");
            foreach (var r in releases)
            {
                table.AddRow(
                    Markup.Escape(r.At.ToLocalTime().ToString("g")),
                    Markup.Escape(r.Kind.ToString().ToLowerInvariant()),
                    OutcomeMarkup(r.Outcome),
                    Markup.Escape(DescribeImages(r)),
                    Markup.Escape(r.Commit is { Length: > 12 } c ? c[..12] : r.Commit ?? ""),
                    Markup.Escape(r.Actor ?? ""),
                    Markup.Escape(Detail(r)));
            }

            AnsiConsole.Write(table);
            return 0;
        }));

        return cmd;
    }

    internal static JsonRelease ToJson(ReleaseRecord r) => new(
        r.Id, JsonOutput.Name(r.Kind), r.At, JsonOutput.Name(r.Outcome), r.Tag, r.Commit, r.FromEnvironment,
        r.Actor, r.ActorSource, r.RunUrl, r.RolloutId, r.Summary, r.DeployifyVersion,
        [.. r.Images.Select(i => new JsonReleaseImage(i.Service, i.Repository, i.Tag, i.Digest))]);

    /// <summary>"3 at 4f2c9e1" or "api, web at v1.2"; "-" for a rollback.</summary>
    private static string DescribeImages(ReleaseRecord r)
    {
        if (r.Images.Count == 0)
        {
            return "-";
        }

        var what = r.Images.Count <= 3 ? string.Join(", ", r.Images.Select(i => i.Service)) : $"{r.Images.Count} images";
        var tag = r.Tag is { Length: > 12 } t && t.All(Uri.IsHexDigit) ? t[..12] : r.Tag;
        return tag is null ? what : $"{what} at {tag}";
    }

    private static string Detail(ReleaseRecord r)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (r.FromEnvironment is not null)
        {
            parts.Add($"from {r.FromEnvironment}");
        }

        if (r.RolloutId is not null)
        {
            parts.Add($"rollout {r.RolloutId}");
        }

        if (r.Summary is not null)
        {
            parts.Add(r.Summary);
        }

        return string.Join("; ", parts);
    }

    private static string OutcomeMarkup(ReleaseOutcome outcome) => outcome switch
    {
        ReleaseOutcome.Succeeded => "[green]succeeded[/]",
        ReleaseOutcome.RolledBack => "[yellow]rolled back[/]",
        _ => "[red]failed[/]"
    };
}
