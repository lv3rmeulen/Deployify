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
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);
            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }
            
            var entries = await target.DiffAsync(manifest, config, outputDir);

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
                    _ => (" ", "dim")
                };
                table.AddRow(
                    $"[{color}]{icon} {e.ServiceName}[/]",
                    $"[{color}]{e.Kind}[/]" + (e.Field is not null ? $" [dim]({e.Field})[/]" : ""));
            }

            AnsiConsole.Write(table);

            var added = entries.Count(e => e.Kind == DiffKind.Added);
            var removed = entries.Count(e => e.Kind == DiffKind.Removed);
            var changed = entries.Count(e => e.Kind == DiffKind.Changed);
            AnsiConsole.MarkupLine($"\nDiff: [green]+{added}[/] [yellow]~{changed}[/] [red]-{removed}[/]");
        });

        return cmd;
    }
}
