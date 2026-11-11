using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;

namespace Deployify.Plugins;

/// <summary>A plugin executable found on disk, and the target ID its file name declares.</summary>
public sealed record DiscoveredPlugin(string TargetId, string ExecutablePath, string Source);

/// <summary>
/// Finds plugin executables by name: <c>deployify-target-&lt;id&gt;</c> serves target
/// <c>&lt;id&gt;</c>. Looks, in order, in the directories of <c>DEPLOYIFY_PLUGIN_PATH</c>,
/// the project's <c>.deployify/plugins</c> folder, the user's <c>~/.deployify/plugins</c> folder
/// (and one level of subfolders, so <c>plugins/example/deployify-target-example.exe</c> works), and PATH.
/// The first match wins. In the plugin folders a framework-dependent <c>.dll</c> also counts;
/// on PATH only real executables do (which includes <c>dotnet tool install -g</c> shims).
/// </summary>
public sealed class PluginCatalog
{
    public const string PluginPathVariable = "DEPLOYIFY_PLUGIN_PATH";

    private readonly Func<string, string?> _getVariable;
    private readonly string? _userDirectory;
    private readonly bool _isWindows;

    public PluginCatalog(
        string? userDirectory = null,
        Func<string, string?>? getVariable = null,
        bool? isWindows = null)
    {
        _getVariable = getVariable ?? Environment.GetEnvironmentVariable;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _userDirectory = userDirectory ?? DefaultUserDirectory();
    }

    /// <summary><c>~/.deployify/plugins</c>.</summary>
    public static string? DefaultUserDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".deployify", "plugins");
    }

    /// <summary>
    /// The plugin that serves <paramref name="target"/>, or null when none is installed.
    /// <paramref name="projectDirectory"/> adds that project's <c>.deployify/plugins</c> folder.
    /// </summary>
    public DiscoveredPlugin? Find(DeploymentTargetType target, string? projectDirectory = null)
        => Discover(projectDirectory).FirstOrDefault(p => string.Equals(p.TargetId, target.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every plugin found, one per target ID (the first match for each).</summary>
    public IReadOnlyList<DiscoveredPlugin> Discover(string? projectDirectory = null)
    {
        var found = new Dictionary<string, DiscoveredPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, source, allowDll, depth) in SearchLocations(projectDirectory))
        {
            foreach (var plugin in Scan(directory, source, allowDll, depth))
            {
                found.TryAdd(plugin.TargetId, plugin);
            }
        }

        return [.. found.Values.OrderBy(p => p.TargetId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The project's plugin folder: <c>&lt;project&gt;/.deployify/plugins</c>.</summary>
    public static string ProjectDirectoryFor(string projectDirectory) => Path.Combine(projectDirectory, ".deployify", "plugins");

    private IEnumerable<(string Directory, string Source, bool AllowDll, int Depth)> SearchLocations(string? projectDirectory)
    {
        foreach (var dir in SplitPath(_getVariable(PluginPathVariable)))
        {
            yield return (dir, PluginPathVariable, true, 1);
        }

        if (projectDirectory is not null)
        {
            yield return (ProjectDirectoryFor(projectDirectory), "project plugins folder", true, 1);
        }

        if (_userDirectory is not null)
        {
            yield return (_userDirectory, "user plugins folder", true, 1);
        }

        foreach (var dir in SplitPath(_getVariable("PATH")))
        {
            yield return (dir, "PATH", false, 0);
        }
    }

    private IEnumerable<DiscoveredPlugin> Scan(string directory, string source, bool allowDll, int depth)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, PluginProtocol.ExecutablePrefix + "*",
                new EnumerationOptions { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files.OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f, StringComparer.Ordinal))
        {
            if (TargetIdFor(file, allowDll) is { } id)
            {
                yield return new DiscoveredPlugin(id, Path.GetFullPath(file), source);
            }
        }
    }

    /// <summary>The target ID a file name declares, or null when the file isn't a runnable plugin.</summary>
    internal string? TargetIdFor(string path, bool allowDll)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(PluginProtocol.ExecutablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extension = Path.GetExtension(name);
        string stem;
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowDll)
            {
                return null;
            }

            stem = Path.GetFileNameWithoutExtension(name);
        }
        else if (_isWindows)
        {
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            stem = Path.GetFileNameWithoutExtension(name);
        }
        else
        {
            // On Unix an executable has no extension; anything with one (.pdb, .json, .deps) isn't it.
            if (extension.Length > 0 || !IsExecutable(path))
            {
                return null;
            }

            stem = name;
        }

        var id = stem[PluginProtocol.ExecutablePrefix.Length..];
        return DeploymentTargetType.TryParse(id, out var target) && !target.IsBuiltIn ? id : null;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SplitPath(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
