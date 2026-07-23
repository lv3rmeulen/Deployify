using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;

namespace Deployify.Commands;

public static class ApplyCommand
{
    public static Command Build()
    {
        var autoApprove = new Option<bool>("--auto-approve", () => false, "Skip confirmation prompt.");

        var cmd = new Command("apply", "Deploy to the target.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(GlobalOptions.DryRun);
        cmd.AddOption(autoApprove);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var dryRun = ctx.ParseResult.GetValueForOption(GlobalOptions.DryRun);
            var approve = ctx.ParseResult.GetValueForOption(autoApprove);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            // PlanAsync writes artefacts to disk AND diffs against the live cluster.
            // If the cluster is unreachable during dry-run we still want to exit 0
            // because the artefact generation phase completed successfully.
            try
            {
	            var plan = await target.PlanAsync(manifest, config, outputDir);
	            AnsiConsole.MarkupLine(
                    $"[bold]Plan:[/] [green]{plan.CreateCount} to create[/], " +
                    $"[yellow]{plan.UpdateCount} to update[/], " +
                    $"[red]{plan.DeleteCount} to delete[/].");
            }
            catch (Exception ex) when (dryRun)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] Could not reach cluster for diff: {ex.Message}");
                AnsiConsole.MarkupLine("[yellow]Dry-run: artefacts written, no changes applied.[/]");
                return;
            }

            if (dryRun) { AnsiConsole.MarkupLine("[yellow]Dry-run: no changes applied.[/]"); return; }

            if (!approve && !AnsiConsole.Confirm($"Apply to [bold cyan]{envName}[/] ({target.DisplayName})?"))
            {
                AnsiConsole.MarkupLine("Aborted.");
                return;
            }

            var results = await target.ApplyAsync(manifest, config, outputDir);

            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);

            foreach (var r in results)
            {
                if (r.Success)
                {
	                AnsiConsole.MarkupLine($"  [green]✓[/] {r.ServiceName}" +
	                                       (r.DeploymentId is not null ? $" [dim]({r.DeploymentId})[/]" : ""));
                }
                else
                {
	                AnsiConsole.MarkupLine($"  [red]✗[/] {r.ServiceName}: {r.Error}");
                }
            }

            AnsiConsole.MarkupLine(failed == 0
                ? $"\n[green]Apply complete.[/] {succeeded} service(s) deployed to [cyan]{envName}[/]."
                : $"\n[yellow]Apply finished with errors.[/] {succeeded} succeeded, {failed} failed.");

            if (failed > 0)
            {
	            ctx.ExitCode = 1;
            }
        });

        return cmd;
    }
}
