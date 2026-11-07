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
        var autoApprove = new Option<bool>("--auto-approve")
        {
            Description = "Skip confirmation prompt.",
            DefaultValueFactory = _ => false
        };

        var cmd = new Command("apply", "Deploy to the target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(autoApprove);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var exitCode = 0;
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            var approve = parseResult.GetValue(autoApprove);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            // PlanAsync writes artifacts to disk AND diffs against the live cluster.
            // If the cluster is unreachable during dry-run we still want to exit 0
            // because the artifact generation phase completed successfully.
            try
            {
                var plan = await target.PlanAsync(manifest, config, outputDir, ct);
                AnsiConsole.MarkupLine(
                    $"[bold]Plan:[/] [green]{plan.CreateCount} to create[/], " +
                    $"[yellow]{plan.UpdateCount} to update[/], " +
                    $"[red]{plan.DeleteCount} to delete[/].");
            }
            catch (Exception ex) when (dryRun)
            {
                AnsiConsole.MarkupLine($"[yellow]![/] Could not reach cluster for diff: {Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("[yellow]Dry-run: artifacts written, no changes applied.[/]");
                return 0;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run: no changes applied.[/]");
                return 0;
            }

            if (!approve && !await ConsolePrompts.ConfirmAsync($"Apply to [bold cyan]{envName}[/] ({target.DisplayName})?", ct))
            {
                AnsiConsole.MarkupLine("Aborted.");
                return 0;
            }

            var results = await target.ApplyAsync(manifest, config, outputDir, ct);

            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);

            foreach (var r in results)
            {
                if (r.Success)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(r.ServiceName)}" +
                        (r.DeploymentId is not null ? $" [dim]({r.DeploymentId})[/]" : ""));
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(r.ServiceName)}: {Markup.Escape(r.Error ?? "")}");
                }
            }

            AnsiConsole.MarkupLine(failed == 0
                ? $"\n[green]Apply complete.[/] {succeeded} service(s) deployed to [cyan]{envName}[/]."
                : $"\n[yellow]Apply finished with errors.[/] {succeeded} succeeded, {failed} failed.");

            if (failed > 0)
            {
                exitCode = 1;
            }

            return exitCode;
        });

        return cmd;
    }
}
