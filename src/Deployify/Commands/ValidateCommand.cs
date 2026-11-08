using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Spectre.Console;

namespace Deployify.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var cmd = new Command("validate", "Validate generated artifacts against the target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var exitCode = 0;
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

            var (_, config, envName, targetType, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/] → {resolved.DisplayAddress}[/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var results = await target.ValidateAsync(manifest, config, outputDir, ct);

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Artifact").AddColumn("Status");

            var allValid = true;
            foreach (var r in results)
            {
                if (r.IsValid)
                {
                    table.AddRow(Markup.Escape(r.ArtifactName), "[green]✓ Valid[/]");
                }
                else
                {
                    allValid = false;
                    table.AddRow(Markup.Escape(r.ArtifactName),
                        $"[red]✗ {r.Errors.Count} error(s)[/]\n" +
                        string.Join("\n", r.Errors.Select(e => $"  • {Markup.Escape(e)}")));
                }
            }

            AnsiConsole.Write(table);
            if (!allValid)
            {
                AnsiConsole.MarkupLine("[red]Validation failed.[/]");
                exitCode = 1;
            }
            else
            {
                AnsiConsole.MarkupLine("[green]All artifacts valid.[/]");
            }

            return exitCode;
        });

        return cmd;
    }
}
