using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Plugins;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary><c>deployify plugin search | install</c>: find plugins in a signed index and install them.</summary>
public static partial class PluginCommand
{
    private static readonly Option<string?> s_index = new("--index")
    {
        Description = $"The plugin index to use instead of the public one ({PluginIndexSource.DefaultUrl}): an https:// URL or a " +
                      $"local path. Default: {PluginIndexSource.UrlVariable}. Another index needs --index-identity or --index-key."
    };

    private static readonly Option<string?> s_indexIdentity = new("--index-identity")
    {
        Description = "Who must have signed the index given with --index (keyless): the exact certificate identity, " +
                      $"such as the workflow that signs it. Default: {PluginIndexSource.IdentityVariable}."
    };

    private static readonly Option<string?> s_indexIssuer = new("--index-issuer")
    {
        Description = $"The OIDC issuer of --index-identity. Default: {PluginIndexSource.IssuerVariable}, else GitHub Actions ({PluginSigner.GitHubActionsIssuer})."
    };

    private static readonly Option<string?> s_indexKey = new("--index-key")
    {
        Description = $"The public key file the index given with --index is signed with, instead of a keyless identity. Default: {PluginIndexSource.KeyVariable}."
    };

    private static readonly Option<bool> s_prerelease = new("--prerelease")
    {
        Description = "Consider pre-release versions when picking the newest one."
    };

    private static void AddIndexOptions(Command cmd)
    {
        cmd.Options.Add(s_index);
        cmd.Options.Add(s_indexIdentity);
        cmd.Options.Add(s_indexIssuer);
        cmd.Options.Add(s_indexKey);
    }

    private static PluginIndexSource IndexSource(ParseResult parseResult, string? url = null)
        => PluginIndexSource.Resolve(
            url ?? parseResult.GetValue(s_index),
            parseResult.GetValue(s_indexIdentity),
            parseResult.GetValue(s_indexIssuer),
            parseResult.GetValue(s_indexKey));

    private static Command BuildSearch()
    {
        var term = new Argument<string?>("term")
        {
            Description = "Text to look for in plugin IDs, names, publishers and descriptions. Default: list every plugin.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cmd = new Command("search",
            "Search the signed plugin index for target plugins. Deployify checks the index's signature before reading it, so this needs cosign.");
        cmd.Arguments.Add(term);
        cmd.Options.Add(s_prerelease);
        AddIndexOptions(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var source = IndexSource(parseResult);
                var index = await new PluginIndexClient().LoadAsync(source, ct);
                var matches = index.Search(parseResult.GetValue(term)).ToList();
                if (matches.Count == 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"[dim]No plugin in {source.Url} matches.[/]");
                    return 0;
                }

                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Target").AddColumn("Plugin").AddColumn("Latest").AddColumn("Publisher").AddColumn("Description");
                foreach (var (id, entry) in matches)
                {
                    var latest = entry.Latest(parseResult.GetValue(s_prerelease));
                    table.AddRow(
                        $"[cyan]{Markup.Escape(id)}[/]",
                        Markup.Escape(entry.Name ?? id),
                        latest is null ? "[dim]none yet[/]" : Markup.Escape(latest.Version!),
                        Markup.Escape(entry.Publisher ?? "-"),
                        Markup.Escape(entry.Description ?? string.Empty));
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]From {source.Url}, signed by {source.Signer.Describe()}. Install one with 'deployify plugin install <target>'.[/]");
                return 0;
            }
            catch (PluginException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }
        });
        return cmd;
    }

    private static Command BuildInstall()
    {
        var plugins = new Argument<string[]>("targets")
        {
            Description = "Plugin target IDs to install, optionally with a version: digitalocean or digitalocean@1.2.0. " +
                          $"Default: every plugin {PluginLockFile.FileName} records as installed from an index, at its locked version.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var local = new Option<bool>("--local")
        {
            Description = "Install into the project's .deployify/plugins folder instead of ~/.deployify/plugins."
        };
        var cmd = new Command("install",
            "Install target plugins from the signed plugin index. Each package must match the SHA-256 the index lists and be " +
            "signed by the publisher the index names; nothing of it runs before both checks pass. In a project, the plugin is then " +
            $"locked in {PluginLockFile.FileName}. Needs cosign.");
        cmd.Arguments.Add(plugins);
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(local);
        cmd.Options.Add(s_prerelease);
        AddIndexOptions(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var project = StateDirectory(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var lockFile = project is null ? null : PluginLockFile.Load(project) ?? new PluginLockFile();

            string pluginsDirectory;
            if (parseResult.GetValue(local))
            {
                if (project is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] --local installs into the project, but {DeploymentSessionResolverStateFile} wasn't found.");
                    return 1;
                }

                pluginsDirectory = PluginCatalog.ProjectDirectoryFor(project);
            }
            else if (PluginCatalog.DefaultUserDirectory() is { } user)
            {
                pluginsDirectory = user;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗[/] There is no home folder to install into; use --local.");
                return 1;
            }

            List<(string Id, string? Version, string? IndexUrl)> requests;
            if (parseResult.GetValue(plugins) is { Length: > 0 } given)
            {
                requests = [.. given.Select(p =>
                {
                    var at = p.IndexOf('@', StringComparison.Ordinal);
                    return at < 0 ? (p, (string?)null, (string?)null) : (p[..at], p[(at + 1)..], (string?)null);
                })];
            }
            else
            {
                requests = lockFile is null ? [] : [.. lockFile.Plugins
                    .Where(p => p.Value.Source?.Index is not null)
                    .Select(p => (p.Key, p.Value.Version, p.Value.Source!.Index))];
                if (requests.Count == 0)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[dim]Name the plugins to install (deployify plugin install <target>); {PluginLockFile.FileName} records none installed from an index.[/]");
                    return project is null ? 1 : 0;
                }
            }

            var client = new PluginIndexClient();
            var installer = new PluginInstaller();
            var indexes = new Dictionary<string, (PluginIndexSource Source, PluginIndex Index)>(StringComparer.Ordinal);
            var catalog = new PluginCatalog();
            var failed = false;
            var lockChanged = false;

            foreach (var (id, version, indexUrl) in requests)
            {
                if (!DeploymentTargetType.TryParse(id, out var target) || target.IsBuiltIn)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] '{id}' isn't a plugin target ID.");
                    failed = true;
                    continue;
                }

                StagedPlugin? staged = null;
                try
                {
                    var source = IndexSource(parseResult, indexUrl);
                    if (!indexes.TryGetValue(source.Url, out var loaded))
                    {
                        loaded = (source, await client.LoadAsync(source, ct));
                        indexes[source.Url] = loaded;
                    }

                    var requested = version;
                    if (requested is null && parseResult.GetValue(s_prerelease))
                    {
                        requested = loaded.Index.Find(target.Value)?.Latest(includePrerelease: true)?.Version;
                    }

                    staged = await installer.StageAsync(loaded.Index, loaded.Source, target.Value, requested, pluginsDirectory, ct: ct);
                    if (staged.Yanked is { } why)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {staged.TargetId} {staged.Version} was withdrawn from the index: {why}");
                    }

                    // The files are checked; now the lock file, before anything runs.
                    var locked = lockFile?.Find(staged.TargetId);
                    var sameVersion = locked is not null && string.Equals(locked.Version, staged.Version, StringComparison.OrdinalIgnoreCase);
                    if (sameVersion && locked!.HashFor(staged.Files.Platform) is { } expected &&
                        !string.Equals(expected, staged.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PluginException(
                            $"{staged.TargetId} {staged.Version} from the index unpacks to SHA-256 {staged.Hash[..12]}…, but {PluginLockFile.FileName} " +
                            $"locks {expected[..Math.Min(12, expected.Length)]}… for {staged.Files.Platform}. It was not installed. If the " +
                            "package was republished on purpose, remove its hash from the lock file and install again.");
                    }

                    // Signed by its publisher, so it may run: check it is what the index says it is.
                    var (info, error) = await ProbeAsync(
                        new DiscoveredPlugin(staged.TargetId, staged.Files.EntryPath, "plugin index") { Files = staged.Files }, locked: null, ct);
                    if (info is null)
                    {
                        throw new PluginException(error!);
                    }

                    if (!string.Equals(info.Plugin.Version, staged.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PluginException(
                            $"{staged.TargetId}: the package for {staged.Version} reports version {info.Plugin.Version ?? "(none)"}. It was not installed.");
                    }

                    if (!info.Targets.Any(t => string.Equals(t.Id, staged.TargetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new PluginException($"{staged.TargetId}: the package doesn't serve target '{staged.TargetId}'. It was not installed.");
                    }

                    await installer.CommitAsync(staged, ct);
                    AnsiConsole.MarkupLineInterpolated(
                        $"[green]✓[/] {staged.TargetId}: {staged.Name} {staged.Version} into {staged.Destination}, signed by {staged.Signer.Describe()}");

                    if (catalog.Find(target, project) is { } found &&
                        !string.Equals(found.ExecutablePath, staged.InstalledEntryPath, StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]![/] Deployify finds another {staged.TargetId} plugin first: {found.ExecutablePath} ({found.Source}). Remove it to use this one.");
                    }

                    if (lockFile is not null)
                    {
                        var hashes = sameVersion && string.Equals(locked!.Plugin, info.Plugin.Name, StringComparison.Ordinal)
                            ? new SortedDictionary<string, string>(locked.Sha256, StringComparer.Ordinal)
                            : new SortedDictionary<string, string>(StringComparer.Ordinal);
                        hashes[staged.Files.Platform] = staged.Hash;
                        lockFile.Plugins[staged.TargetId] = new PluginLock
                        {
                            Plugin = info.Plugin.Name,
                            Version = info.Plugin.Version,
                            Sha256 = hashes,
                            Source = new PluginLockSource { Index = loaded.Source.Url, Signer = staged.Signer.Describe() }
                        };
                        lockChanged = true;
                    }
                }
                catch (PluginException ex)
                {
                    if (staged is not null)
                    {
                        installer.Discard(staged);
                    }

                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                    failed = true;
                }
            }

            if (lockChanged)
            {
                await lockFile!.SaveAsync(project!, ct);
                AnsiConsole.MarkupLineInterpolated($"[dim]Locked in {PluginLockFile.PathFor(project!)}. Commit it so every machine uses the same plugins.[/]");
            }
            else if (project is null && !failed)
            {
                AnsiConsole.MarkupLine("[dim]Not in a project, so nothing was locked. Run 'deployify plugin install' or 'deployify plugin lock' in the project that uses it.[/]");
            }

            return failed ? 1 : 0;
        });
        return cmd;
    }

    private static string DeploymentSessionResolverStateFile => Core.Deployments.DeploymentSessionResolver.StateFileName;
}
