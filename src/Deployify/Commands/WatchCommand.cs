using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Watches the project directory for source file changes and automatically
/// re-runs generate + plan so you can see what would change in real time.
///
/// Watches: **/*.cs, **/*.json, **/*.yaml, **/*.yml, *.csproj
/// Debounce: 500 ms (configurable with --debounce)
/// Does not apply — use <c>deployify apply</c> to deploy after reviewing the plan.
/// </summary>
public static class WatchCommand
{
    public static Command Build()
    {
        var debounceOpt = new Option<int>("--debounce",
            () => 500,
            "Milliseconds to wait after the last file change before re-running (default: 500).");
        var noGenerateOpt = new Option<bool>("--no-generate",
            () => false,
            "Skip re-generating artefacts; only re-run the plan diff against the cluster.");

        var cmd = new Command("watch",
            "Watch for source changes and automatically re-run generate + plan.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(debounceOpt);
        cmd.AddOption(noGenerateOpt);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var debounceMs = ctx.ParseResult.GetValueForOption(debounceOpt);
            var noGenerate = ctx.ParseResult.GetValueForOption(noGenerateOpt);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[bold]Deployify Watch[/] — [cyan]{envName}[/] / [cyan]{target.DisplayName}[/]");
            AnsiConsole.MarkupLine($"[dim]Watching: {directory}[/]");
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop.[/]\n");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            // Run immediately on start
            await RunCycleAsync(resolved, target, config, outputDir, noGenerate);

            using var watcher = BuildWatcher(directory);
            var pendingRun = (Task?)null;
            var runLock = new SemaphoreSlim(1, 1);

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;

            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }

            AnsiConsole.MarkupLine("\n[dim]Watch stopped.[/]");
            return;

            void OnChanged(object _, FileSystemEventArgs e)
            {
                // Ignore output directory changes (we write there ourselves)
                if (e.FullPath.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase))
                {
	                return;
                }

                if (e.FullPath.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                {
	                return;
                }

                if (e.FullPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
	                return;
                }

                // Debounce: cancel any pending run, schedule a new one
                runLock.Wait(cts.Token);
                try
                {
                    pendingRun = pendingRun?.Status is TaskStatus.Running ? pendingRun : ScheduleRunAsync();
                }
                finally { runLock.Release(); }
            }

            async Task ScheduleRunAsync()
            {
                await Task.Delay(debounceMs, cts.Token).ConfigureAwait(false);
                AnsiConsole.MarkupLine("\n[dim]Change detected — re-running...[/]");
                await RunCycleAsync(resolved, target, config, outputDir, noGenerate);
            }
        });

        return cmd;
    }

    private static async Task RunCycleAsync(
        ResolvedContext resolved,
        IDeploymentTarget target,
        EnvironmentConfig config,
        string outputDir,
        bool noGenerate)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        AnsiConsole.MarkupLine($"[dim]── {timestamp} ──────────────────────────────[/]");

        var manifest = await resolved.LoadManifestAsync();
        if (manifest is null)
        {
            AnsiConsole.MarkupLine("[yellow]! Could not load manifest — skipping cycle.[/]");
            return;
        }

        // Generate artefacts
        if (!noGenerate)
        {
            try
            {
                var paths = await target.GenerateAsync(manifest, config, outputDir);
                AnsiConsole.MarkupLine($"[green]✓[/] Generated {paths.Count} artefact(s)");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Generate failed: {ex.Message}");
                return;
            }
        }

        // Plan (best-effort — cluster may be unreachable)
        try
        {
            var plan = await target.PlanAsync(manifest, config, outputDir);
            var hasChanges = plan.CreateCount + plan.UpdateCount + plan.DeleteCount > 0;

            if (!hasChanges)
            {
                AnsiConsole.MarkupLine("[dim]No changes.[/]");
                return;
            }

            if (plan.CreateCount > 0)
            {
	            AnsiConsole.MarkupLine($"[green]+ {plan.CreateCount} to create[/]: " +
	                                   string.Join(", ", plan.ToCreate.Select(o => o.ToString()!)));
            }

            if (plan.UpdateCount > 0)
            {
	            AnsiConsole.MarkupLine($"[yellow]~ {plan.UpdateCount} to update[/]: " +
	                                   string.Join(", ", plan.ToUpdate.Select(o => o.ToString()!)));
            }

            if (plan.DeleteCount > 0)
            {
	            AnsiConsole.MarkupLine($"[red]- {plan.DeleteCount} to delete[/]: " +
	                                   string.Join(", ", plan.ToDelete));
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]! Plan diff unavailable: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[dim]Artefacts written. Cluster unreachable for diff.[/]");
        }
    }

    private static FileSystemWatcher BuildWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        // Watch source files most likely to affect generated artefacts
        foreach (var filter in new[] { "*.cs", "*.csproj", "*.json", "*.yaml", "*.yml", "*.toml" })
        {
	        watcher.Filters.Add(filter);
        }

        return watcher;
    }
}
