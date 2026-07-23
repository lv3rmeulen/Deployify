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

            var (_, config, envName, _, target, _) = resolved;
            AnsiConsole.MarkupLine($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }
            
            var deps = target.GetDependencies(manifest, config);

            var allNames = manifest.Resources.Keys.ToList();
            var dependants = allNames.ToDictionary(n => n, _ => new List<string>());
            foreach (var dep in deps)
            {
	            if (dependants.ContainsKey(dep.From))
	            {
		            dependants[dep.From].Add(dep.To);
	            }
            }

            var roots = allNames.Where(n => deps.All(d => d.To != n)).ToList();
            var tree = new Tree("[dim]Target cluster[/]");

            foreach (var root in roots)
            {
	            AddChildren(tree.AddNode($"[bold cyan]{root}[/]"), root, dependants);
            }

            AnsiConsole.Write(tree);
        });

        return cmd;
    }

    private static void AddChildren(TreeNode parent, string name, Dictionary<string, List<string>> dependants)
    {
        if (!dependants.TryGetValue(name, out var children) || children.Count == 0)
        {
	        return;
        }

        foreach (var child in children)
        {
	        AddChildren(parent.AddNode($"[bold cyan]{child}[/]"), child, dependants);
        }
    }
}
