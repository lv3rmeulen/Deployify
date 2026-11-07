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
        var debounceOpt = new Option<int>("--debounce")
        {
            Description = "Milliseconds to wait after the last file change before re-running (default: 500).",
            DefaultValueFactory = _ => 500
        };
        var noGenerateOpt = new Option<bool>("--no-generate")
        {
            Description = "Skip re-generating artifacts; only re-run the plan diff against the cluster.",
            DefaultValueFactory = _ => false
        };

        var cmd = new Command("watch",
            "Watch for source changes and automatically re-run generate + plan.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(debounceOpt);
        cmd.Options.Add(noGenerateOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var debounceMs = parseResult.GetValue(debounceOpt);
            var noGenerate = parseResult.GetValue(noGenerateOpt);

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

            AnsiConsole.MarkupLine($"[bold]Deployify Watch[/] — [cyan]{envName}[/] / [cyan]{target.DisplayName}[/]");
            AnsiConsole.MarkupLine($"[dim]Watching: {directory}[/]");
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop.[/]\n");

            // Ctrl+C cancels the command's token; everything below stops with it.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Run immediately on start
            await RunCycleAsync(resolved, target, config, outputDir, noGenerate, cts.Token);

            using var watcher = BuildWatcher(directory);
            var gate = new object();
            CancellationTokenSource? debounceCts = null;
            var cycleLock = new SemaphoreSlim(1, 1);
            var outputPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir)) + Path.DirectorySeparatorChar;

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            AnsiConsole.MarkupLine("\n[dim]Watch stopped.[/]");
            return 0;

            void OnChanged(object obj, FileSystemEventArgs e)
            {
                if (IsIgnored(e.FullPath, outputPrefix))
                {
                    return;
                }

                // Debounce: every change restarts the quiet period; only the last one runs.
                CancellationToken token;
                lock (gate)
                {
                    _ = debounceCts?.CancelAsync();
                    debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    token = debounceCts.Token;
                }

                _ = RunAfterQuietPeriodAsync(token);
            }

            async Task RunAfterQuietPeriodAsync(CancellationToken token)
            {
                try
                {
                    await Task.Delay(debounceMs, token);
                    // Never run two cycles at once; a change during a cycle queues one more.
                    await cycleLock.WaitAsync(cts.Token);
                    try
                    {
                        AnsiConsole.MarkupLine("\n[dim]Change detected — re-running...[/]");
                        await RunCycleAsync(resolved, target, config, outputDir, noGenerate, token);
                    }
                    finally
                    {
                        cycleLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    /* superseded by a newer change, or stopping */
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                }
            }
        });

        return cmd;
    }

    private static async Task RunCycleAsync(
        ResolvedContext resolved,
        IDeploymentTarget target,
        EnvironmentConfig config,
        string outputDir,
        bool noGenerate,
        CancellationToken ct)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        AnsiConsole.MarkupLine($"[dim]── {timestamp} ──────────────────────────────[/]");

        var manifest = await resolved.LoadManifestAsync(ct);
        if (manifest is null)
        {
            AnsiConsole.MarkupLine("[yellow]! Could not load manifest — skipping cycle.[/]");
            return;
        }

        // Generate artifacts
        if (!noGenerate)
        {
            try
            {
                var paths = await target.GenerateAsync(manifest, config, outputDir, ct);
                AnsiConsole.MarkupLine($"[green]✓[/] Generated {paths.Count} artifact(s)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Generate failed: {Markup.Escape(ex.Message)}");
                return;
            }
        }

        // Plan (best-effort — cluster may be unreachable)
        try
        {
            var plan = await target.PlanAsync(manifest, config, outputDir, ct);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]! Plan diff unavailable: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Artifacts written. Cluster unreachable for diff.[/]");
        }
    }

    /// <summary>
    /// Changes Deployify makes itself (generated output, the state file, build output)
    /// must not trigger another cycle, or watch would loop forever.
    /// </summary>
    private static bool IsIgnored(string fullPath, string outputPrefix)
    {
        if (fullPath.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sep = Path.DirectorySeparatorChar;
        foreach (var dir in new[] { "obj", "bin", ".git", ".vs", ".idea", "node_modules" })
        {
            if (fullPath.Contains($"{sep}{dir}{sep}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var file = Path.GetFileName(fullPath);
        return file.StartsWith("deployify-state.json", StringComparison.OrdinalIgnoreCase);
    }

    private static FileSystemWatcher BuildWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        // Watch source files most likely to affect generated artifacts
        foreach (var filter in new[] { "*.cs", "*.csproj", "*.json", "*.yaml", "*.yml", "*.toml" })
        {
            watcher.Filters.Add(filter);
        }

        return watcher;
    }
}
