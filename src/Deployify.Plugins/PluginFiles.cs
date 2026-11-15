using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Deployify.Plugins;

/// <summary>
/// The files a plugin's lock hash covers, and the platform that hash is recorded under.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A plugin in a folder of its own (<c>plugins/example/deployify-target-example.exe</c>):
/// every file in that folder, so its dependencies are covered too.</item>
/// <item>A <c>dotnet tool</c> shim: the tool's own folder in the tool store, not the shim, which
/// differs per machine.</item>
/// <item>Anything else (a loose file in a plugin folder, an executable on PATH): that file.</item>
/// </list>
/// A framework-dependent plugin is the same on every platform, so its hash is recorded under
/// <see cref="AnyPlatform"/>; anything else is recorded under the runtime ID of the machine
/// (<c>win-x64</c>, <c>linux-x64</c>, …), so one lock file can hold a hash per platform.
/// </remarks>
public sealed record PluginFiles
{
    /// <summary>The platform key for a plugin that is the same on every platform.</summary>
    public const string AnyPlatform = "any";

    private const string ToolStoreFolder = ".store";
    private const string ToolSettingsFile = "DotnetToolSettings.xml";

    /// <summary>The file that identifies the plugin: the executable, <c>.dll</c> or tool entry point.</summary>
    public required string EntryPath { get; init; }

    /// <summary>The folder that is hashed as a whole, or null when only <see cref="EntryPath"/> is.</summary>
    public string? Folder { get; init; }

    /// <summary>The platform key the hash is recorded under in the lock file.</summary>
    public required string Platform { get; init; }

    /// <summary>The runtime ID of this machine, the platform key for platform-specific plugins.</summary>
    public static string CurrentPlatform => RuntimeInformation.RuntimeIdentifier;

    /// <summary>Just this file.</summary>
    public static PluginFiles ForFile(string path)
    {
        var full = Path.GetFullPath(path);
        return new PluginFiles { EntryPath = full, Platform = PlatformFor(full) };
    }

    /// <summary>A plugin that has <paramref name="path"/>'s folder to itself: the whole folder.</summary>
    public static PluginFiles ForOwnFolder(string path)
    {
        var full = Path.GetFullPath(path);
        return new PluginFiles { EntryPath = full, Folder = Path.GetDirectoryName(full), Platform = PlatformFor(full) };
    }

    /// <summary>
    /// An executable that may share its folder with others (PATH, the top of a plugin folder): the
    /// tool behind it when it is a <c>dotnet tool</c> shim, otherwise the file itself.
    /// </summary>
    public static PluginFiles ForSharedFolder(string path)
        => ResolveToolShim(Path.GetFullPath(path)) ?? ForFile(path);

    /// <summary>What the hash covers, for messages.</summary>
    public string Describe() => Folder is null ? EntryPath : $"{Folder}{Path.DirectorySeparatorChar} (every file)";

    /// <summary>SHA-256 of what this covers, lowercase hex.</summary>
    /// <remarks>
    /// For a folder: SHA-256 over one line per file, <c>&lt;sha256&gt;  &lt;relative/path&gt;\n</c>,
    /// sorted by path (ordinal, <c>/</c> separators), so it is the same on every platform.
    /// </remarks>
    public string ComputeHash()
    {
        if (Folder is null)
        {
            return PluginLockFile.HashFile(EntryPath);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        var lines = new StringBuilder();
        foreach (var (relative, full) in Directory.EnumerateFiles(Folder, "*", options)
                     .Select(f => (Relative: Path.GetRelativePath(Folder, f).Replace('\\', '/'), Full: f))
                     .OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            lines.Append(PluginLockFile.HashFile(full)).Append("  ").Append(relative).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
    }

    /// <summary>A framework-dependent <c>.dll</c> runs anywhere; everything else is platform-specific.</summary>
    private static string PlatformFor(string entryPath)
        => entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? AnyPlatform : CurrentPlatform;

    /// <summary>
    /// <c>dotnet tool install</c> puts a launcher next to a <c>.store</c> folder, with the tool in
    /// <c>.store/&lt;id&gt;/&lt;version&gt;/&lt;id&gt;/&lt;version&gt;/tools/&lt;tfm&gt;/&lt;any|rid&gt;/</c>
    /// beside a DotnetToolSettings.xml that names the command. Null when this isn't such a launcher.
    /// </summary>
    internal static PluginFiles? ResolveToolShim(string shimPath)
    {
        var shimDirectory = Path.GetDirectoryName(shimPath);
        if (shimDirectory is null)
        {
            return null;
        }

        var store = Path.Combine(shimDirectory, ToolStoreFolder);
        if (!Directory.Exists(store))
        {
            return null;
        }

        var command = Path.GetFileName(shimPath);
        if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            command = command[..^4];
        }

        var candidates = new List<(string Folder, string Entry)>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true };
            foreach (var settings in Directory.EnumerateFiles(store, ToolSettingsFile, options))
            {
                if (EntryPointFor(settings, command) is { } entry)
                {
                    candidates.Add((Path.GetDirectoryName(settings)!, entry));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Several versions in the store: the launcher has the relative path to its entry point built in.
        var match = candidates.Count == 1
            ? candidates[0]
            : candidates.FirstOrDefault(c => LauncherPointsAt(shimPath, Path.GetRelativePath(shimDirectory, c.Entry)));
        if (match.Folder is null)
        {
            return null;
        }

        var (folder, entryPath) = match;

        var platform = string.Equals(Path.GetFileName(folder), AnyPlatform, StringComparison.OrdinalIgnoreCase)
            ? AnyPlatform
            : CurrentPlatform;
        return new PluginFiles { EntryPath = Path.GetFullPath(entryPath), Folder = Path.GetFullPath(folder), Platform = platform };
    }

    private static string? EntryPointFor(string settingsPath, string command)
    {
        try
        {
            var commands = XDocument.Load(settingsPath).Root?.Element("Commands")?.Elements("Command") ?? [];
            foreach (var element in commands)
            {
                if (string.Equals((string?)element.Attribute("Name"), command, StringComparison.OrdinalIgnoreCase) &&
                    (string?)element.Attribute("EntryPoint") is { Length: > 0 } entryPoint)
                {
                    var entry = Path.Combine(Path.GetDirectoryName(settingsPath)!, entryPoint);
                    return File.Exists(entry) ? entry : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
        }

        return null;
    }

    private static bool LauncherPointsAt(string shimPath, string relativeEntry)
    {
        try
        {
            var bytes = File.ReadAllBytes(shimPath);
            return ContainsUtf8(bytes, relativeEntry.Replace('\\', '/')) || ContainsUtf8(bytes, relativeEntry.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsUtf8(byte[] haystack, string needle)
        => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
}
