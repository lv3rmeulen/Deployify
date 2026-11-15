using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary><c>deployify plugin list | lock</c>: see and pin the target plugins a project uses.</summary>
public static class PluginCommand
{
    public static Command Build()
    {
        var cmd = new Command("plugin", "Show and lock the target plugins this project can use.");
        cmd.Subcommands.Add(BuildList());
        cmd.Subcommands.Add(BuildLock());
        return cmd;
    }

    private static Command BuildList()
    {
        var check = new Option<bool>("--check")
        {
            Description = "Start each plugin and show what it reports (name, version, capabilities)."
        };
        var cmd = new Command("list", "List the target plugins Deployify can find, and where it found them.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(check);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var project = StateDirectory(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var catalog = new PluginCatalog();
            var plugins = catalog.Discover(project);
            var lockFile = project is null ? null : PluginLockFile.Load(project);

            if (plugins.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No target plugins found.[/]");
                var userFolder = PluginCatalog.DefaultUserDirectory() ?? "~/.deployify/plugins";
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Deployify looks for executables named deployify-target-<id> in {PluginCatalog.PluginPathVariable}, <project>/.deployify/plugins, {userFolder} and PATH.[/]");
                return lockFile is { Plugins.Count: > 0 } ? 1 : 0;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Target").AddColumn("Executable").AddColumn("Found in").AddColumn("Locked");
            if (parseResult.GetValue(check))
            {
                table.AddColumn("Plugin").AddColumn("Capabilities");
            }

            var failed = false;
            foreach (var plugin in plugins)
            {
                var locked = lockFile?.Find(plugin.TargetId);
                var row = new List<string>
                {
                    Markup.Escape(plugin.TargetId),
                    Markup.Escape(plugin.ExecutablePath),
                    Markup.Escape(plugin.Source),
                    LockStatus(locked, plugin.Files)
                };

                if (parseResult.GetValue(check))
                {
                    var (info, error) = await ProbeAsync(plugin, locked, ct);
                    if (info is null)
                    {
                        failed = true;
                        row.Add($"[red]✗ {Markup.Escape(error!)}[/]");
                        row.Add(string.Empty);
                    }
                    else
                    {
                        var target = info.Targets.FirstOrDefault(t => string.Equals(t.Id, plugin.TargetId, StringComparison.OrdinalIgnoreCase));
                        row.Add(Markup.Escape($"{info.Plugin.Name} {info.Plugin.Version} (protocol {info.ProtocolVersion})"));
                        row.Add(target is null
                            ? "[red]doesn't serve this target[/]"
                            : Markup.Escape(target.Capabilities.Count == 0 ? "-" : string.Join(", ", target.Capabilities)));
                        failed |= target is null;
                    }
                }

                table.AddRow(row.ToArray());
            }

            AnsiConsole.Write(table);

            foreach (var missing in lockFile?.Plugins.Keys.Where(k => !plugins.Any(p => string.Equals(p.TargetId, k, StringComparison.OrdinalIgnoreCase))) ?? [])
            {
                failed = true;
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {PluginLockFile.FileName} locks target [cyan]{missing}[/], but its plugin isn't installed.");
            }

            return failed ? 1 : 0;
        });
        return cmd;
    }

    private static Command BuildLock()
    {
        var targets = new Argument<string[]>("targets")
        {
            Description = "Plugin target IDs to lock. Default: every plugin target the project's environments use.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var cmd = new Command("lock",
            $"Record the plugin version and the SHA-256 of its files each target uses in {PluginLockFile.FileName}. " +
            "Deployify refuses to start a plugin target without one. A platform-specific plugin gets a hash per platform: " +
            "run this on each platform that deploys.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Arguments.Add(targets);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var project = StateDirectory(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            if (project is null)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {DeploymentSessionResolver.StateFileName} not found. Run 'deployify init' first.");
                return 1;
            }

            var ids = parseResult.GetValue(targets) is { Length: > 0 } given
                ? given.ToList()
                : await UsedPluginTargetsAsync(project, ct);
            if (ids.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No environment uses a plugin target; nothing to lock.[/]");
                return 0;
            }

            var catalog = new PluginCatalog();
            var lockFile = PluginLockFile.Load(project) ?? new PluginLockFile();
            var failed = false;
            foreach (var id in ids)
            {
                if (!DeploymentTargetType.TryParse(id, out var target) || target.IsBuiltIn)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] '{id}' isn't a plugin target ID.");
                    failed = true;
                    continue;
                }

                if (catalog.Find(target, project) is not { } plugin)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] No plugin found for [cyan]{id}[/]; install it first ('deployify plugin list' shows where Deployify looks).");
                    failed = true;
                    continue;
                }

                // Hash first: what is recorded is what was there before anything of it ran.
                var hash = plugin.Files.ComputeHash();
                var (info, error) = await ProbeAsync(plugin, locked: null, ct);
                if (info is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {id}: {error}");
                    failed = true;
                    continue;
                }

                // The same plugin version locked on another platform keeps that platform's hash.
                var previous = lockFile.Find(target.Value);
                var hashes = previous is not null &&
                             string.Equals(previous.Plugin, info.Plugin.Name, StringComparison.Ordinal) &&
                             string.Equals(previous.Version, info.Plugin.Version, StringComparison.OrdinalIgnoreCase)
                    ? new SortedDictionary<string, string>(previous.Sha256, StringComparer.Ordinal)
                    : new SortedDictionary<string, string>(StringComparer.Ordinal);
                hashes[plugin.Files.Platform] = hash;

                var entry = new PluginLock { Plugin = info.Plugin.Name, Version = info.Plugin.Version, Sha256 = hashes };
                lockFile.Plugins[target.Value] = entry;
                var others = hashes.Count > 1 ? $" (also locked for {string.Join(", ", hashes.Keys.Where(k => k != plugin.Files.Platform))})" : "";
                AnsiConsole.MarkupLineInterpolated(
                    $"[green]✓[/] {target.Value}: {entry.Plugin} {entry.Version ?? "(no version)"}, {plugin.Files.Platform} sha256 {hash[..12]}… of {plugin.Files.Describe()}{others}");
            }

            await lockFile.SaveAsync(project, ct);
            AnsiConsole.MarkupLineInterpolated($"[dim]Wrote {PluginLockFile.PathFor(project)}. Commit it so every machine uses the same plugins.[/]");
            return failed ? 1 : 0;
        });
        return cmd;
    }

    private static string? StateDirectory(string projectPath)
    {
        var start = File.Exists(projectPath) ? Path.GetDirectoryName(Path.GetFullPath(projectPath))! : Path.GetFullPath(projectPath);
        return DeploymentSessionResolver.FindStateDirectory(start);
    }

    private static async Task<List<string>> UsedPluginTargetsAsync(string project, CancellationToken ct)
    {
        var state = await new StateManager().LoadFileAsync(project, ct);
        return [.. state.Environments.Values
            .Select(e => e.Target)
            .Where(t => !t.IsBuiltIn)
            .Select(t => t.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The "Locked" column: whether this plugin would start without --allow-unverified.</summary>
    private static string LockStatus(PluginLock? locked, PluginFiles files)
    {
        if (locked is null)
        {
            return "[red]no (unverified)[/]";
        }

        var version = Markup.Escape(locked.Version ?? "any version");
        return locked.HashFor(files.Platform) is null
            ? $"[yellow]{version}, no hash for {Markup.Escape(files.Platform)} (unverified)[/]"
            : $"{version} + hash";
    }

    /// <summary>
    /// Starts a plugin, reads its handshake and stops it again. An unlocked plugin is allowed: this
    /// is for looking at plugins, not deploying with them. A hash that doesn't match still fails.
    /// </summary>
    private static async Task<(Plugin.Contracts.InitializeResult? Info, string? Error)> ProbeAsync(
        DiscoveredPlugin plugin, PluginLock? locked, CancellationToken ct)
    {
        var host = new PluginHost(
            new PluginLaunchSpec
            {
                ExecutablePath = plugin.ExecutablePath,
                Source = plugin.Source,
                Lock = locked,
                Files = plugin.Files,
                Unverified = UnverifiedPluginPolicy.Allow
            },
            NullLoggerFactory.Instance);
        try
        {
            return ((await host.GetConnectionAsync(ct)).Info, null);
        }
        catch (PluginException ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
