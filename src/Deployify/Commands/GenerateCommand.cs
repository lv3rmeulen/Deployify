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
            "Generate deployment artifacts (HCL, manifests, compose files) without contacting the cluster.");
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

            IReadOnlyList<string> paths;
            try
            {
                paths = await AnsiConsole.Status().Spinner(Spinner.Known.Dots2)
                    .StartAsync("Generating artifacts...",
                        _ => target.GenerateAsync(manifest, config, outputDir, ct));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Generation failed: {Markup.Escape(ex.Message)}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]✓[/] Generated {paths.Count} artifact(s) → [dim]{Markup.Escape(outputDir)}[/]\n");
            foreach (var path in paths)
            {
                AnsiConsole.MarkupLine($"  [dim]{Path.GetFileName(path)}[/]");
            }

            return 0;
        });

        return cmd;
    }
}
