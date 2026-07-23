using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

public static class PlanCommand
{
    public static Command Build()
    {
        var cmd = new Command("plan", "Preview changes that would be applied to the target.");
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

            AnsiConsole.MarkupLine("[bold]Deployify Plan[/]\n");

            // PlanAsync writes artefacts to disk first, then diffs against the live cluster.
            // We always exit 0 if generation succeeded — a cluster being unreachable is
            // a warning, not a failure, for the plan command.
            PlanResult plan;
            try
            {
                plan = await target.PlanAsync(manifest, config, outputDir);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] Could not reach cluster for diff: {ex.Message}");
                AnsiConsole.MarkupLine("[dim]Artefacts written. Run again when the cluster is reachable to see the full diff.[/]");
                return;
            }

            PrintSection(plan.ToCreate.Select(o => o.ToString()!).ToList(), "[green]+ Create[/]", Color.Green);
            PrintSection(plan.ToUpdate.Select(o => o.ToString()!).ToList(), "[yellow]~ Update[/]", Color.Yellow);
            PrintSection(plan.ToDelete, "[red]- Delete[/]", Color.Red);

            AnsiConsole.MarkupLine(
                $"Plan: [green]{plan.CreateCount} to create[/], " +
                $"[yellow]{plan.UpdateCount} to update[/], " +
                $"[red]{plan.DeleteCount} to delete[/].");

            if (plan is { CreateCount: 0, UpdateCount: 0, DeleteCount: 0 })
            {
	            AnsiConsole.MarkupLine("[dim]No changes. Everything is up to date.[/]");
            }
        });

        return cmd;
    }

    private static void PrintSection(IReadOnlyList<string> items, string heading, Color color)
    {
        if (items.Count == 0)
        {
	        return;
        }

        AnsiConsole.MarkupLine(heading);
        foreach (var item in items)
        {
	        AnsiConsole.MarkupLine($"  [bold {color}]{item}[/]");
        }

        AnsiConsole.WriteLine();
    }
}
