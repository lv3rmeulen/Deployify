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
            "Generate deployment artifacts (Nomad jobs, manifests, compose files) without contacting the cluster.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
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

            // The errors were reported when the manifest was loaded. Generating anyway would write
            // each parameter's placeholder into the artifacts as its value.
            if (resolved.ManifestAudit.MissingValues.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] {resolved.ManifestAudit.MissingValues.Count} parameter(s) have no value; nothing was generated.");
                return 1;
            }

            IReadOnlyList<string> paths;
            try
            {
                paths = await ConsoleStatus.RunAsync("Generating artifacts...",
                    () => target.GenerateAsync(manifest, config, outputDir, ct));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Generation failed: {ex.Message}");
                return 1;
            }

            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Generated {paths.Count} artifact(s) → [dim]{outputDir}[/]\n");
            foreach (var path in paths)
            {
                AnsiConsole.MarkupLineInterpolated($"  [dim]{Path.GetFileName(path)}[/]");
            }

            return 0;
        });

        return cmd;
    }
}
