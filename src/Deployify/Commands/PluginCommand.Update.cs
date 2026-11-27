using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Plugins;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary><c>deployify plugin update</c>: move the plugins a project locked to their newest compatible version.</summary>
public static partial class PluginCommand
{
    /// <summary>Exit code of <c>--check</c> when there is something to update (1 means a check failed).</summary>
    public const int UpdatesAvailableExitCode = 2;

    internal static readonly Option<bool> MajorOption = new("--major")
    {
        Description = "Also move to a new major version (for 0.x: a new minor version), which may have breaking changes. " +
                      "Default: stay on versions without breaking changes."
    };

    internal static readonly Option<bool> CheckOption = new("--check")
    {
        Description = $"Only report what would be updated. Exits {UpdatesAvailableExitCode} when something can be updated, 0 when nothing can, 1 when a check failed."
    };

    /// <summary>What <c>plugin update</c> and <c>update</c> were asked to do.</summary>
    internal sealed record PluginUpdateRequest(IReadOnlyList<string> Targets, bool Major, bool Prerelease, bool DryRun, bool Check);

    private static Command BuildUpdate()
    {
        var targets = new Argument<string[]>("targets")
        {
            Description = $"Plugin target IDs to update. Default: every plugin {PluginLockFile.FileName} records as installed from an index.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var cmd = new Command("update",
            $"Update the plugins {PluginLockFile.FileName} records as installed from a plugin index to their newest version without " +
            "breaking changes, and lock the new versions. Each package is checked like 'plugin install' checks it: the index's hash, " +
            "its publisher's signature, and what the plugin reports. Needs cosign.");
        cmd.Arguments.Add(targets);
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(MajorOption);
        cmd.Options.Add(s_prerelease);
        cmd.Options.Add(CheckOption);
        cmd.Options.Add(GlobalOptions.DryRun);
        AddIndexOptions(cmd);

        cmd.SetAction((parseResult, ct) => UpdatePluginsAsync(
            parseResult,
            parseResult.GetValue(GlobalOptions.ProjectPath)!,
            new PluginUpdateRequest(
                parseResult.GetValue(targets) ?? [],
                parseResult.GetValue(MajorOption),
                parseResult.GetValue(s_prerelease),
                parseResult.GetValue(GlobalOptions.DryRun),
                parseResult.GetValue(CheckOption)),
            ct));
        return cmd;
    }

    /// <summary>
    /// Updates the project's index-installed plugins. The parse result must come from a command
    /// with the index options (<see cref="AddIndexOptions"/>). Returns 0, 1 when something failed,
    /// or <see cref="UpdatesAvailableExitCode"/> for <c>--check</c> with updates available.
    /// </summary>
    internal static async Task<int> UpdatePluginsAsync(ParseResult parseResult, string projectPath, PluginUpdateRequest request, CancellationToken ct)
    {
        var project = StateDirectory(projectPath);
        if (project is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]✗[/] Not in a Deployify project ({DeploymentSessionResolverStateFile} wasn't found), so there is no {PluginLockFile.FileName} to update. Outside a project, install the version you want with 'deployify plugin install <target>@<version>'.");
            return 1;
        }

        var lockFile = PluginLockFile.Load(project);
        var fromIndex = lockFile?.Plugins
            .Where(p => p.Value.Source?.Index is not null)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) ?? [];

        var failed = false;
        List<string> selected;
        if (request.Targets.Count > 0)
        {
            selected = [];
            foreach (var id in request.Targets)
            {
                if (fromIndex.ContainsKey(id))
                {
                    selected.Add(fromIndex.Keys.First(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)));
                }
                else
                {
                    if (lockFile?.Find(id) is null)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {PluginLockFile.FileName} doesn't lock '{id}'. Install it with 'deployify plugin install {id}'.");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]✗[/] '{id}' wasn't installed from a plugin index, so Deployify doesn't know where to look for a newer version.");
                    }

                    failed = true;
                }
            }
        }
        else
        {
            selected = [.. fromIndex.Keys];
            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]{PluginLockFile.FileName} records no plugin installed from an index; nothing to update.[/]");
                return 0;
            }
        }

        var run = new IndexInstall(parseResult, project, lockFile, PluginCatalog.ProjectDirectoryFor(project));
        var updates = new List<(string Id, string To, string? IndexUrl)>();
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Target").AddColumn("Locked").AddColumn("Update").AddColumn("Note");
        foreach (var id in selected)
        {
            var locked = fromIndex[id];
            var current = locked.Version ?? "0.0.0";
            PluginIndexEntry? entry;
            try
            {
                entry = (await run.LoadIndexAsync(locked.Source!.Index, ct)).Index.Find(id);
            }
            catch (PluginException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {id}: {ex.Message}");
                failed = true;
                continue;
            }

            if (entry is null)
            {
                table.AddRow($"[cyan]{Markup.Escape(id)}[/]", Markup.Escape(current), "[red]-[/]", $"[red]{Markup.Escape(locked.Source!.Index!)} no longer lists it[/]");
                failed = true;
                continue;
            }

            var update = entry.UpdateFor(current, request.Prerelease, request.Major);
            var notes = new List<string>();
            if (entry.FindVersion(current)?.Yanked is { } why)
            {
                notes.Add($"[yellow]{Markup.Escape(current)} was withdrawn: {Markup.Escape(why)}[/]");
            }

            if (!request.Major && entry.UpdateFor(current, request.Prerelease, crossMajor: true) is { } breaking &&
                !string.Equals(breaking.Version, update?.Version, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"[dim]{Markup.Escape(breaking.Version!)} may break things; --major moves to it[/]");
            }

            if (update is not null)
            {
                updates.Add((id, update.Version!, locked.Source!.Index));
            }

            table.AddRow(
                $"[cyan]{Markup.Escape(id)}[/]",
                Markup.Escape(current),
                update is null ? "[dim]up to date[/]" : $"[green]{Markup.Escape(update.Version!)}[/]",
                string.Join(" ", notes));
        }

        if (table.Rows.Count > 0)
        {
            AnsiConsole.Write(table);
        }

        if (updates.Count == 0)
        {
            if (!failed)
            {
                AnsiConsole.MarkupLine("[green]✓[/] The plugins are up to date.");
            }

            return failed ? 1 : 0;
        }

        if (request.Check)
        {
            return failed ? 1 : UpdatesAvailableExitCode;
        }

        if (request.DryRun)
        {
            AnsiConsole.MarkupLine("[dim]Dry run: nothing was installed.[/]");
            return failed ? 1 : 0;
        }

        var catalog = new PluginCatalog();
        var projectPlugins = Path.GetFullPath(PluginCatalog.ProjectDirectoryFor(project));
        foreach (var (id, to, indexUrl) in updates)
        {
            // Into the folder it is installed in now: the project's, else the user's.
            var installed = DeploymentTargetType.TryParse(id, out var target) ? catalog.Find(target, project) : null;
            run.PluginsDirectory = installed is not null && IsUnder(installed.ExecutablePath, projectPlugins)
                ? projectPlugins
                : PluginCatalog.DefaultUserDirectory() ?? projectPlugins;
            failed |= !await run.InstallAsync(id, to, indexUrl, ct);
        }

        if (run.LockChanged)
        {
            await lockFile!.SaveAsync(project, ct);
            AnsiConsole.MarkupLineInterpolated($"[dim]Locked in {PluginLockFile.PathFor(project)}. Commit it so every machine uses the same plugins.[/]");
        }

        return failed ? 1 : 0;
    }

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
