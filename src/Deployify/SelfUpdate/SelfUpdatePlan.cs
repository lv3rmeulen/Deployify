using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Deployify.Core.Versioning;

namespace Deployify.SelfUpdate;

/// <summary>
/// How to move this Deployify to <see cref="Version"/>: the <c>dotnet tool update</c> to run, and
/// whether it must run after Deployify has exited (<see cref="Detach"/>).
/// </summary>
/// <param name="Detach">
/// On Windows a running program's files are locked, so <c>dotnet tool update</c> can't replace a
/// global or --tool-path Deployify while it runs: it is started in the background to wait for this
/// process to exit first. A local tool's new version goes to a new folder in the NuGet cache, so it
/// can be updated in place.
/// </param>
internal sealed record SelfUpdatePlan(string Version, string Dotnet, IReadOnlyList<string> Args, bool Detach)
{
    /// <summary>Where a background update writes its output.</summary>
    public static string LogPath => Path.Combine(Path.GetTempPath(), "deployify-update.log");

    /// <summary>The command, for printing.</summary>
    public string CommandLine => string.Join(' ', new[] { "dotnet" }.Concat(Args).Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    /// <summary>The newest version <paramref name="current"/> can update to, or null when it is the newest.</summary>
    public static string? PickVersion(string current, IEnumerable<string> available, bool includePrerelease, bool crossMajor)
        => available
            .Where(v => VersionOrder.IsUpdate(current, v, includePrerelease, crossMajor))
            .OrderByDescending(v => v, VersionOrder.Comparer)
            .FirstOrDefault();

    /// <summary>The newest version of any kind that would only be picked with --major, or null.</summary>
    public static string? NewerMajor(string current, IEnumerable<string> available, bool includePrerelease)
        => PickVersion(current, available, includePrerelease, crossMajor: true) is { } any && !VersionOrder.IsCompatible(current, any) ? any : null;

    /// <summary>The plan for <paramref name="installation"/>, or null when it can't update itself.</summary>
    /// <param name="source">The feed the version was found in; passed on unless it's nuget.org, which dotnet uses anyway.</param>
    public static SelfUpdatePlan? For(ToolInstallation installation, string version, string source, bool windows, string? dotnet = null)
    {
        var args = installation.Kind switch
        {
            ToolInstallKind.Global => new List<string> { "tool", "update", "--global", ToolInstallation.PackageId },
            ToolInstallKind.ToolPath => ["tool", "update", "--tool-path", installation.Location, ToolInstallation.PackageId],
            ToolInstallKind.Local => ["tool", "update", ToolInstallation.PackageId, "--tool-manifest", installation.Location],
            _ => null
        };
        if (args is null)
        {
            return null;
        }

        args.AddRange(["--version", version]);
        if (!string.Equals(source, PackageVersionSource.DefaultSource, StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--add-source", source]);
        }

        return new SelfUpdatePlan(
            version,
            dotnet ?? DotnetHost(),
            args,
            Detach: windows && installation.Kind is ToolInstallKind.Global or ToolInstallKind.ToolPath);
    }

    /// <summary>
    /// The PowerShell script a detached update runs: wait for <paramref name="processId"/> to exit,
    /// then run the update with its output in <see cref="LogPath"/>.
    /// </summary>
    public string BackgroundScript(int processId, string logPath)
    {
        var command = string.Join(' ', Args.Select(Quote));
        return string.Join('\n',
            $"Wait-Process -Id {processId} -Timeout 120 -ErrorAction SilentlyContinue",
            $"& {Quote(Dotnet)} {command} *> {Quote(logPath)}",
            "exit $LASTEXITCODE");

        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary><see cref="BackgroundScript"/> for <c>powershell -EncodedCommand</c>.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>The dotnet that runs this Deployify when there is one (set by the SDK), else whatever is on the PATH.</summary>
    private static string DotnetHost()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
}
