using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Deployify.Commands.Json;
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
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(serviceOpt);

        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "explain", async json =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var serviceFilter = parseResult.GetValue(serviceOpt);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            json?.Use(resolved);
            var (_, config, envName, _, target, _) = resolved;
            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var explanations = await target.ExplainAsync(manifest, config, ct);

            if (serviceFilter is not null)
            {
                explanations =
                [
                    .. explanations
                        .Where(e => e.Name.Equals(serviceFilter, StringComparison.OrdinalIgnoreCase))
                ];
            }

            if (json is not null)
            {
                json.Result = ToJson(explanations);
            }

            if (!explanations.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No matching services.[/]");
                return 0;
            }

            foreach (var exp in explanations)
            {
                var grid = new Grid().AddColumn().AddColumn();
                // Values come from the manifest and config, so they're escaped; only the Image row adds markup.
                void Row(string l, string v) => grid.AddRow($"[dim]{Markup.Escape(l)}[/]", Markup.Escape(v));
                grid.AddRow("[dim]Image[/]", $"{Markup.Escape(exp.Image ?? "n/a")}:[yellow]{Markup.Escape(exp.ImageTag ?? "")}[/]");
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
                    Header = new PanelHeader($" [bold cyan]{Markup.Escape(exp.Name)}[/] "),
                    Border = BoxBorder.Rounded,
                    Padding = new Padding(1, 0)
                });
                AnsiConsole.WriteLine();
            }

            return 0;
        }));

        return cmd;
    }

    /// <summary>
    /// The <c>result</c> of <c>explain --output json</c>. Environment variables stay out, as in
    /// the text output: they can hold resolved secrets.
    /// </summary>
    internal static JsonExplain ToJson(IEnumerable<ServiceExplanation> explanations) => new(
        [.. explanations.Select(e => new JsonService(e.Name, e.Image, e.ImageTag, e.Count,
            e.Ports, e.Dependencies, new SortedDictionary<string, string>(e.AdditionalInfo, StringComparer.Ordinal)))]);
}
