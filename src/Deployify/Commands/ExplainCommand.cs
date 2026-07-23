using System;
using System.CommandLine;
using System.Linq;
using Spectre.Console;

namespace Deployify.Commands;

public static class ExplainCommand
{
    public static Command Build()
    {
        var serviceOpt = new Option<string?>(["--service", "-s"], "Filter to a specific service.");

        var cmd = new Command("explain", "Display a human-readable summary of all services.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(serviceOpt);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var serviceFilter = ctx.ParseResult.GetValueForOption(serviceOpt);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;
            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            var explanations = target.Explain(manifest, config);

            if (serviceFilter is not null)
            {
	            explanations = explanations
		            .Where(e => e.Name.Equals(serviceFilter, StringComparison.OrdinalIgnoreCase))
		            .ToList();
            }

            if (!explanations.Any()) { AnsiConsole.MarkupLine("[yellow]No matching services.[/]"); return; }

            foreach (var exp in explanations)
            {
                var grid = new Grid().AddColumn().AddColumn();
                void Row(string l, string v) => grid.AddRow($"[dim]{l}[/]", v);
                Row("Image", $"{exp.Image ?? "n/a"}:[yellow]{exp.ImageTag}[/]");
                Row("Count", exp.Count.ToString());
                if (exp.Ports.Count > 0)
                {
	                Row("Ports", string.Join(", ", exp.Ports));
                }

                if (exp.Dependencies.Count > 0)
                {
	                Row("Depends on", string.Join(", ", exp.Dependencies));
                }

                foreach (var (k, v) in exp.AdditionalInfo)
                {
	                Row(k, v);
                }

                AnsiConsole.Write(new Panel(grid)
                {
                    Header = new PanelHeader($" [bold cyan]{exp.Name}[/] "),
                    Border = BoxBorder.Rounded,
                    Padding = new Padding(1, 0)
                });
                AnsiConsole.WriteLine();
            }
        });

        return cmd;
    }
}
