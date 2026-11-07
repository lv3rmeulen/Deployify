using System;
using System.CommandLine;
using System.Linq;
using Spectre.Console;

namespace Deployify.Commands;

public static class ExplainCommand
{
    public static Command Build()
    {
        var serviceOpt = new Option<string?>("--service", "-s")
        {
            Description = "Filter to a specific service."
        };

        var cmd = new Command("explain", "Display a human-readable summary of all services.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(serviceOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var serviceFilter = parseResult.GetValue(serviceOpt);

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

            var (_, config, envName, _, target, _) = resolved;
            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var explanations = target.Explain(manifest, config);

            if (serviceFilter is not null)
            {
                explanations =
                [
                    .. explanations
                        .Where(e => e.Name.Equals(serviceFilter, StringComparison.OrdinalIgnoreCase))
                ];
            }

            if (!explanations.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No matching services.[/]");
                return 0;
            }

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

            return 0;
        });

        return cmd;
    }
}
