using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using Deployify.Commands.Json;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Deployify.Commands;

public static class DiffCommand
{
    public static Command Build()
    {
        var summaryOpt = new Option<bool>("--summary")
        {
            Description = "Print only the table of services and the counts, without the changed values " +
                          "(for Kubernetes and Helm, the kubectl diff / helm diff output). " +
                          "--output json includes the values either way."
        };

        var cmd = new Command("diff", "Show differences between local manifest and the running target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(summaryOpt);
        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "diff", async json =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            json?.Use(resolved);
            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);
            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var entries = await target.DiffAsync(manifest, config, outputDir, ct);

            // Generating the artifacts resolved the parameters, so the registry holds every secret this diff can contain.
            var secrets = resolved.Services.GetService<SecretRegistry>()?.Snapshot() ?? new Dictionary<string, SensitiveValue>();
            if (json is not null)
            {
                json.Result = ToJson(entries, secrets);
            }

            PrintText(entries, secrets, summary: parseResult.GetValue(summaryOpt));
            return 0;
        }));

        return cmd;
    }

    /// <summary>
    /// The text output: a table of what differs, then the changed values unless
    /// <paramref name="summary"/> is set (<c>--summary</c>), then the counts.
    /// </summary>
    internal static void PrintText(IReadOnlyList<DiffEntry> entries, IReadOnlyDictionary<string, SensitiveValue> secrets, bool summary)
    {
        if (entries.All(e => e.Kind == DiffKind.Unchanged))
        {
            AnsiConsole.MarkupLine("[green]No differences. Target matches local manifest.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Service").AddColumn("Change");

        foreach (var e in entries.OrderBy(e => e.Kind))
        {
            var (icon, color) = e.Kind switch
            {
                DiffKind.Added => ("+", "green"),
                DiffKind.Removed => ("-", "red"),
                DiffKind.Changed => ("~", "yellow"),
                DiffKind.Unknown => ("?", "blue"),
                _ => (" ", "dim")
            };
            var detail = e.Kind == DiffKind.Unknown ? SecretLeakScanner.Redact(e.Reason, secrets) : e.Field;
            table.AddRow(
                $"[{color}]{icon} {Markup.Escape(e.ServiceName)}[/]",
                $"[{color}]{e.Kind}[/]" + (detail is not null ? $" [dim]({Markup.Escape(detail)})[/]" : ""));
        }

        AnsiConsole.Write(table);
        if (!summary)
        {
            PrintValues(entries.OrderBy(e => e.Kind), secrets);
        }

        var added = entries.Count(e => e.Kind == DiffKind.Added);
        var removed = entries.Count(e => e.Kind == DiffKind.Removed);
        var changed = entries.Count(e => e.Kind == DiffKind.Changed);
        var unknown = entries.Count(e => e.Kind == DiffKind.Unknown);
        AnsiConsole.MarkupLine($"\nDiff: [green]+{added}[/] [yellow]~{changed}[/] [red]-{removed}[/]" +
                               (unknown > 0 ? $" [blue]?{unknown} could not be checked[/]" : ""));
    }

    /// <summary>
    /// Below the table, what changed for each entry the target reported values for, with every
    /// value in <paramref name="secrets"/> masked. A single value prints on one line
    /// (<c>worker instance_count: 1 → 2</c>); a rendered diff (Kubernetes, Helm) prints under its
    /// service, with added lines green and removed lines red.
    /// </summary>
    internal static void PrintValues(IEnumerable<DiffEntry> entries, IReadOnlyDictionary<string, SensitiveValue> secrets)
    {
        var withValues = entries.Where(e => e.OldValue is not null || e.NewValue is not null).ToList();
        if (withValues.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        foreach (var e in withValues)
        {
            var oldValue = SecretLeakScanner.Redact(e.OldValue, secrets);
            var newValue = SecretLeakScanner.Redact(e.NewValue, secrets);
            var heading = $"[bold]{Markup.Escape(e.ServiceName)}[/]" + (e.Field is null ? "" : $" [dim]{Markup.Escape(e.Field)}[/]");

            if (IsMultiLine(oldValue) || IsMultiLine(newValue))
            {
                AnsiConsole.MarkupLine(heading);
                PrintBlock(oldValue);
                PrintBlock(newValue);
                AnsiConsole.WriteLine();
                continue;
            }

            var change = (oldValue, newValue) switch
            {
                ({ } o, { } n) => $"[red]{Markup.Escape(o)}[/] → [green]{Markup.Escape(n)}[/]",
                (null, { } n) => Markup.Escape(n),
                ({ } o, null) => Markup.Escape(o),
                _ => ""
            };
            AnsiConsole.MarkupLine($"{heading}: {change}");
        }
    }

    private static bool IsMultiLine(string? value) => value is not null && value.Contains('\n');

    /// <summary>A unified diff, indented, colored by each line's prefix.</summary>
    private static void PrintBlock(string? text)
    {
        if (text is null)
        {
            return;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var color = line switch
            {
                _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => "dim",
                _ when line.StartsWith('+') => "green",
                _ when line.StartsWith('-') => "red",
                _ when line.StartsWith("@@", StringComparison.Ordinal) => "cyan",
                _ => "dim"
            };
            AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(line)}[/]");
        }
    }

    /// <summary>
    /// The <c>result</c> of <c>diff --output json</c>, in the order the table shows. The old and
    /// new values are what the target reports: a field's value, or for Kubernetes and Helm the
    /// whole rendered diff. kubectl and helm-diff already hide Secret data; on top of that, every
    /// value in <paramref name="secrets"/> is masked, raw or base64-encoded.
    /// </summary>
    internal static JsonDiff ToJson(IEnumerable<DiffEntry> entries, IReadOnlyDictionary<string, SensitiveValue>? secrets = null)
    {
        secrets ??= new Dictionary<string, SensitiveValue>();
        var list = entries.OrderBy(e => e.Kind).ToList();
        return new JsonDiff(
            Identical: list.All(e => e.Kind == DiffKind.Unchanged),
            Entries: [.. list.Select(e => new JsonDiffEntry(e.ServiceName, e.Kind switch
            {
                DiffKind.Added => JsonDiffKind.Added,
                DiffKind.Removed => JsonDiffKind.Removed,
                DiffKind.Changed => JsonDiffKind.Changed,
                DiffKind.Unknown => JsonDiffKind.Unknown,
                _ => JsonDiffKind.Unchanged
            }, e.Field, SecretLeakScanner.Redact(e.OldValue, secrets), SecretLeakScanner.Redact(e.NewValue, secrets),
                SecretLeakScanner.Redact(e.Reason, secrets)))]);
    }
}
