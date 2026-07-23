using System;
using System.CommandLine;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

public static class PublishCommand
{
    public static Command Build()
    {
        var imageTagOpt = new Option<string?>(["-i", "--image-tag"], "Override image tag for this publish run.");
        var runtimeOpt = new Option<ContainerRuntime?>("--container-runtime",
            "Override container runtime for this run: docker | podman.");

        var cmd = new Command("publish", "Build and push service images or artefacts.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(GlobalOptions.DryRun);
        cmd.AddOption(imageTagOpt);
        cmd.AddOption(runtimeOpt);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var dryRun = ctx.ParseResult.GetValueForOption(GlobalOptions.DryRun);
            var tagOverride = ctx.ParseResult.GetValueForOption(imageTagOpt);
            var runtimeOverride = ctx.ParseResult.GetValueForOption(runtimeOpt);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOverride); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;

            if (tagOverride is not null)
            {
	            config.ImageTag = tagOverride;
            }

            if (runtimeOverride is not null)
            {
	            config.ContainerRuntime = runtimeOverride.Value;
            }

            AnsiConsole.MarkupLine(
                $"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/] Runtime: [cyan]{config.ContainerRuntime}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run: would publish the following project services:[/]");
                foreach (var (name, res) in manifest.Resources)
                {
	                if (res.Type.StartsWith("project.v"))
	                {
		                AnsiConsole.MarkupLine($"  [cyan]{name}[/]");
	                }
                }

                return;
            }

            await AnsiConsole.Status().Spinner(Spinner.Known.Dots2)
                .StartAsync("Publishing...", async _ =>
                    await target.PublishAsync(manifest, config, resolved.AppHostDirectory));

            AnsiConsole.MarkupLine("[green]Publish complete.[/]");
        });

        return cmd;
    }
}
