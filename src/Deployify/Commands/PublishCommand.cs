using System;
using System.CommandLine;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

public static class PublishCommand
{
    public static Command Build()
    {
        var imageTagOpt = new Option<string?>("--image-tag", "-i")
        {
            Description = "Override image tag for this publish run."
        };
        var runtimeOpt = new Option<ContainerRuntime?>("--container-runtime")
        {
            Description = "Override container runtime for this run: docker | podman."
        };

        var cmd = new Command("publish", "Build and push service images or artifacts.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(imageTagOpt);
        cmd.Options.Add(runtimeOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOverride = parseResult.GetValue(GlobalOptions.Target);
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            var tagOverride = parseResult.GetValue(imageTagOpt);
            var runtimeOverride = parseResult.GetValue(runtimeOpt);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOverride, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            var (_, config, envName, _, target, directory) = resolved;

            if (tagOverride is not null)
            {
                config.ImageTag = tagOverride;
            }

            if (runtimeOverride is not null)
            {
                config.ContainerRuntime = runtimeOverride.Value;
            }

            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/] Runtime: [cyan]{config.ContainerRuntime}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run: would publish the following project services:[/]");
                foreach (var (name, res) in manifest.Resources)
                {
                    if (res.IsProject)
                    {
                        AnsiConsole.MarkupLineInterpolated($"  [cyan]{name}[/]");
                    }
                }

                return 0;
            }

            await AnsiConsole.Status().Spinner(Spinner.Known.Dots2)
                .StartAsync("Publishing...", async _ =>
                    await target.PublishAsync(manifest, config, resolved.AppHostDirectory, ct));

            AnsiConsole.MarkupLine("[green]Publish complete.[/]");

            return 0;
        });

        return cmd;
    }
}
