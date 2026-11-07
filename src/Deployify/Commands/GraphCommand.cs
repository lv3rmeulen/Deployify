using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Spectre.Console;

namespace Deployify.Commands;

public static class GraphCommand
{
    public static Command Build()
    {
        var cmd = new Command("graph", "Display a dependency graph of all services.");
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
            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var deps = target.GetDependencies(manifest, config);

            var allNames = manifest.Resources.Where(r => r.Value.IsDeployable).Select(r => r.Key).ToList();
            var dependants = allNames.ToDictionary(n => n, _ => new List<string>());
            foreach (var dep in deps)
            {
                if (dependants.ContainsKey(dep.From) && !dependants[dep.From].Contains(dep.To))
                {
                    dependants[dep.From].Add(dep.To);
                }
            }

            var tree = new Tree("[dim]Target cluster[/]");
            var shown = new HashSet<string>();

            // Start from services nothing depends on; anything left over sits on a cycle.
            var roots = allNames.Where(n => deps.All(d => d.To != n)).ToList();
            foreach (var root in roots.Concat(allNames))
            {
                if (!shown.Add(root))
                {
                    continue;
                }

                AddChildren(tree.AddNode($"[bold cyan]{Markup.Escape(root)}[/]"), root, dependants, [root], shown);
            }

            AnsiConsole.Write(tree);

            return 0;
        });

        return cmd;
    }

    private static void AddChildren(
        TreeNode parent, string name, Dictionary<string, List<string>> dependants,
        HashSet<string> path, HashSet<string> shown)
    {
        if (!dependants.TryGetValue(name, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            if (path.Contains(child))
            {
                // A dependency cycle: show it once instead of recursing forever.
                parent.AddNode($"[bold cyan]{Markup.Escape(child)}[/] [yellow](cycle)[/]");
                continue;
            }

            shown.Add(child);
            path.Add(child);
            AddChildren(parent.AddNode($"[bold cyan]{Markup.Escape(child)}[/]"), child, dependants, path, shown);
            path.Remove(child);
        }
    }
}
