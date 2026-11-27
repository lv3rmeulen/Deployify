using System;
using System.IO;
using System.Linq;

namespace Deployify.SelfUpdate;

/// <summary>How the running Deployify was installed, which decides how it can update itself.</summary>
internal enum ToolInstallKind
{
    /// <summary><c>dotnet tool install --global deployify</c>.</summary>
    Global,

    /// <summary><c>dotnet tool install --tool-path &lt;folder&gt; deployify</c>.</summary>
    ToolPath,

    /// <summary>A local tool, listed in a <c>dotnet-tools.json</c> manifest.</summary>
    Local,

    /// <summary>Run from the NuGet cache without being installed (<c>dnx deployify</c>, or a local tool whose manifest isn't found).</summary>
    NotInstalled,

    /// <summary>Anything else: a build from source, <c>dotnet run</c>, a container image.</summary>
    Unknown
}

/// <param name="Location">The tools folder (Global, ToolPath), the manifest (Local), or where Deployify runs from.</param>
internal sealed record ToolInstallation(ToolInstallKind Kind, string Location)
{
    public const string PackageId = "deployify";

    public string Describe() => Kind switch
    {
        ToolInstallKind.Global => "a global .NET tool",
        ToolInstallKind.ToolPath => $"a .NET tool in {Location}",
        ToolInstallKind.Local => $"a local .NET tool, pinned in {Location}",
        ToolInstallKind.NotInstalled => "a .NET tool run from the NuGet cache without being installed",
        _ => $"a build in {Location}, not a .NET tool"
    };

    public static ToolInstallation DetectCurrent()
        => Detect(
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } cliHome
                ? cliHome
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Works out the installation from where Deployify's files are (<paramref name="baseDirectory"/>).
    /// A tool installed with --global or --tool-path runs from
    /// <c>&lt;tools&gt;/.store/deployify/&lt;version&gt;/…</c>; a local tool, and one run with
    /// <c>dnx</c>, from the NuGet cache: <c>&lt;packages&gt;/deployify/&lt;version&gt;/tools/…</c>.
    /// </summary>
    /// <param name="workingDirectory">Where to start looking for a local tool manifest.</param>
    /// <param name="cliHome">The folder whose <c>.dotnet/tools</c> holds global tools (DOTNET_CLI_HOME, else the home folder).</param>
    public static ToolInstallation Detect(string baseDirectory, string workingDirectory, string? cliHome)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var parts = full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None);

        // <tools>/.store/deployify/<version>/deployify/<version>/tools/<tfm>/any
        for (var i = parts.Length - 2; i > 0; i--)
        {
            if (string.Equals(parts[i], ".store", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i + 1], PackageId, StringComparison.OrdinalIgnoreCase))
            {
                var tools = string.Join(Path.DirectorySeparatorChar, parts[..i]);
                tools = tools.Length == 0 ? Path.DirectorySeparatorChar.ToString() : tools;
                var global = cliHome is null ? null : Path.Combine(cliHome, ".dotnet", "tools");
                return global is not null && SamePath(tools, global)
                    ? new ToolInstallation(ToolInstallKind.Global, tools)
                    : new ToolInstallation(ToolInstallKind.ToolPath, tools);
            }
        }

        // <packages>/deployify/<version>/tools/<tfm>/<rid or any>
        for (var i = parts.Length - 3; i > 0; i--)
        {
            if (string.Equals(parts[i], PackageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i + 2], "tools", StringComparison.OrdinalIgnoreCase))
            {
                return FindManifest(workingDirectory) is { } manifest
                    ? new ToolInstallation(ToolInstallKind.Local, manifest)
                    : new ToolInstallation(ToolInstallKind.NotInstalled, full);
            }
        }

        return new ToolInstallation(ToolInstallKind.Unknown, full);
    }

    /// <summary>The nearest <c>dotnet-tools.json</c> (or <c>.config/dotnet-tools.json</c>) that lists Deployify, the way <c>dotnet tool</c> looks for one.</summary>
    internal static string? FindManifest(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, ".config", "dotnet-tools.json"), Path.Combine(dir.FullName, "dotnet-tools.json") })
            {
                if (File.Exists(candidate) && ListsDeployify(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool ListsDeployify(string manifest)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest),
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("tools", out var tools) &&
                   tools.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   tools.EnumerateObject().Any(t => string.Equals(t.Name, PackageId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
