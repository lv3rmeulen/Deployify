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

            AnsiConsole.MarkupLine("[bold]Deployify Plan[/]\n");

            // PlanAsync writes artifacts to disk first, then diffs against the live cluster.
            // Targets treat an unreachable cluster as "everything is new" and log a
            // warning, so an exception here means planning itself failed.
            PlanResult plan;
            try
            {
                plan = await target.PlanAsync(manifest, config, outputDir, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Plan failed: {ex.Message}");
                return 1;
            }

            PrintSection(plan.ToCreate, "[green]+ Create[/]", Color.Green);
            PrintSection(plan.ToUpdate, "[yellow]~ Update[/]", Color.Yellow);
            PrintSection(plan.ToDelete, "[red]- Delete[/]", Color.Red);
            PlanOutput.PrintUnknown(plan);

            AnsiConsole.MarkupLine(PlanOutput.Summary(plan));

            // Only when every service is known to match: an unchecked one isn't "up to date".
            if (plan.IsUpToDate)
            {
                AnsiConsole.MarkupLine("[dim]No changes. Everything is up to date.[/]");
            }

            return 0;
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
            AnsiConsole.MarkupLineInterpolated($"  [bold {color}]{item}[/]");
        }

        AnsiConsole.WriteLine();
    }
}
