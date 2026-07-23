using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using Spectre.Console;

namespace Deployify.Commands;

public static class GenerateCommand
{
    public static Command Build()
    {
        var cmd = new Command("generate",
            "Generate deployment artefacts (HCL, manifests, compose files) without contacting the cluster.");
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

            IReadOnlyList<string> paths;
            try
            {
                paths = await AnsiConsole.Status().Spinner(Spinner.Known.Dots2)
                    .StartAsync("Generating artefacts...",
                        _ => target.GenerateAsync(manifest, config, outputDir));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Generation failed: {ex.Message}");
                ctx.ExitCode = 1;
                return;
            }

            AnsiConsole.MarkupLine($"[green]✓[/] Generated {paths.Count} artefact(s) → [dim]{outputDir}[/]\n");
            foreach (var path in paths)
            {
	            AnsiConsole.MarkupLine($"  [dim]{Path.GetFileName(path)}[/]");
            }
        });

        return cmd;
    }
}
