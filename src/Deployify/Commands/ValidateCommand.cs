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
        var cmd = new Command("validate", "Validate generated artefacts against the target.");
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

            var (_, config, envName, targetType, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/] → {resolved.DisplayAddress}[/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            var results = await target.ValidateAsync(manifest, config, outputDir);

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Artefact").AddColumn("Status");

            var allValid = true;
            foreach (var r in results)
            {
                if (r.IsValid)
                {
	                table.AddRow(r.ArtefactName, "[green]✓ Valid[/]");
                }
                else
                {
                    allValid = false;
                    table.AddRow(r.ArtefactName,
                        $"[red]✗ {r.Errors.Count} error(s)[/]\n" +
                        string.Join("\n", r.Errors.Select(e => $"  • {e}")));
                }
            }

            AnsiConsole.Write(table);
            if (!allValid) { AnsiConsole.MarkupLine("[red]Validation failed.[/]"); ctx.ExitCode = 1; }
            else
            {
	            AnsiConsole.MarkupLine("[green]All artefacts valid.[/]");
            }
        });

        return cmd;
    }
}
