using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

public static class DiffCommand
{
    public static Command Build()
    {
        var cmd = new Command("diff", "Show differences between local manifest and the running target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);
            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var entries = await target.DiffAsync(manifest, config, outputDir, ct);

            if (entries.All(e => e.Kind == DiffKind.Unchanged))
            {
                AnsiConsole.MarkupLine("[green]No differences. Target matches local manifest.[/]");
                return 0;
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
                var detail = e.Kind == DiffKind.Unknown ? e.Reason : e.Field;
                table.AddRow(
                    $"[{color}]{icon} {Markup.Escape(e.ServiceName)}[/]",
                    $"[{color}]{e.Kind}[/]" + (detail is not null ? $" [dim]({Markup.Escape(detail)})[/]" : ""));
            }

            AnsiConsole.Write(table);

            var added = entries.Count(e => e.Kind == DiffKind.Added);
            var removed = entries.Count(e => e.Kind == DiffKind.Removed);
            var changed = entries.Count(e => e.Kind == DiffKind.Changed);
            var unknown = entries.Count(e => e.Kind == DiffKind.Unknown);
            AnsiConsole.MarkupLine($"\nDiff: [green]+{added}[/] [yellow]~{changed}[/] [red]-{removed}[/]" +
                                   (unknown > 0 ? $" [blue]?{unknown} could not be checked[/]" : ""));

            return 0;
        });

        return cmd;
    }
}
