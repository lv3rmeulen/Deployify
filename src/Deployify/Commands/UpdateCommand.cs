using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Deployify.Core.Processes;
using Deployify.Core.Versioning;
using Deployify.SelfUpdate;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// <c>deployify update</c>: update the project's plugins and check its Deployify.Hosting package;
/// <c>deployify update --self</c>: update Deployify itself.
/// </summary>
public static class UpdateCommand
{
    private const string HostingPackage = "Deployify.Hosting";

    public static Command Build()
    {
        var self = new Option<bool>("--self")
        {
            Description = "Update Deployify itself instead of the project, with 'dotnet tool update'. Works for a global, --tool-path " +
                          "or local .NET tool. On Windows a global or --tool-path Deployify is updated in the background once this " +
                          $"command has exited, with its output in %TEMP%\\deployify-update.log."
        };
        var source = new Option<string?>("--source")
        {
            Description = $"With --self: the NuGet feed to look for Deployify in, a v3 service index URL or a folder of .nupkg files. " +
                          $"Default: {PackageVersionSource.SourceVariable}, else {PackageVersionSource.DefaultSource}."
        };
        var cmd = new Command("update",
            "Update the project's plugins to their newest version without breaking changes (like 'deployify plugin update') and check " +
            $"that the AppHost's {HostingPackage} package matches this Deployify. With --self, update Deployify itself.");
        cmd.Options.Add(self);
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(PluginCommand.MajorOption);
        cmd.Options.Add(new Option<bool>("--prerelease") { Description = "Consider pre-release versions. Default: only when the current version is one." });
        cmd.Options.Add(PluginCommand.CheckOption);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(source);
        PluginCommand.AddIndexOptions(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var major = parseResult.GetValue(PluginCommand.MajorOption);
            var prerelease = parseResult.GetValue<bool>("--prerelease");
            var check = parseResult.GetValue(PluginCommand.CheckOption);
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);

            if (parseResult.GetValue(self))
            {
                return await UpdateSelfAsync(parseResult.GetValue(source), major, prerelease, check, dryRun, ct);
            }

            if (parseResult.GetValue(source) is not null)
            {
                AnsiConsole.MarkupLine("[red]✗[/] --source only applies to --self; the plugins are updated from the index they were installed from.");
                return 1;
            }

            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var result = await PluginCommand.UpdatePluginsAsync(
                parseResult, projectPath, new PluginCommand.PluginUpdateRequest([], major, prerelease, dryRun, check), ct);
            CheckHosting(projectPath);
            return result;
        });
        return cmd;
    }

    private static async Task<int> UpdateSelfAsync(string? sourceOption, bool major, bool prerelease, bool check, bool dryRun, CancellationToken ct)
    {
        var current = CliVersion.Current;
        var installation = ToolInstallation.DetectCurrent();
        var source = PackageVersionSource.Resolve(sourceOption);

        string[] available;
        try
        {
            available = [.. await new PackageVersionSource().GetVersionsAsync(source, ToolInstallation.PackageId, ct)];
        }
        catch (SelfUpdateException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
            return 1;
        }

        var target = SelfUpdatePlan.PickVersion(current, available, prerelease, major);
        if (!major && SelfUpdatePlan.NewerMajor(current, available, prerelease) is { } breaking)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Deployify {breaking} is out and may have breaking changes; 'deployify update --self --major' moves to it. See the changelog first.[/]");
        }

        if (target is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Deployify {current} is up to date.");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"Deployify {current} → [green]{target}[/] ({installation.Describe()}).");
        AnsiConsole.MarkupLineInterpolated($"[dim]What changed: https://github.com/lv3rmeulen/Deployify/blob/main/CHANGELOG.md[/]");
        if (check)
        {
            return PluginCommand.UpdatesAvailableExitCode;
        }

        var plan = SelfUpdatePlan.For(installation, target, source, OperatingSystem.IsWindows());
        if (plan is null)
        {
            if (installation.Kind == ToolInstallKind.NotInstalled)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Deployify isn't installed, so there is nothing to update. Run that version with: dnx {ToolInstallation.PackageId}@{target}");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]![/] Deployify runs from {installation.Location}, not as a .NET tool, so it can't update itself. Install the tool with: dotnet tool install --global {ToolInstallation.PackageId}");
            }

            return 1;
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Dry run: would run {plan.CommandLine}[/]");
            return 0;
        }

        if (plan.Detach)
        {
            try
            {
                StartInBackground(plan);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Couldn't start the update ({ex.Message}). Run it yourself once Deployify has exited: {plan.CommandLine}");
                return 1;
            }

            AnsiConsole.WriteLine("Updating.");
            return 0;
        }

        ProcessResult result;
        try
        {
            result = await new ProcessRunner().RunAsync(new ProcessSpec(plan.Dotnet, plan.Args) { Timeout = TimeSpan.FromMinutes(5) }, ct);
        }
        catch (Exception ex) when (ex is ToolNotFoundException or TimeoutException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
            return 1;
        }

        if (!result.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {plan.CommandLine} failed:");
            AnsiConsole.WriteLine((result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim());
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Updated Deployify to {target}.");
        if (installation.Kind == ToolInstallKind.Local)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]{installation.Location} now pins {target}; commit it so everyone uses it.[/]");
        }

        return 0;
    }

    /// <summary>Runs the update from a hidden PowerShell that waits for this process to exit first.</summary>
    private static void StartInBackground(SelfUpdatePlan plan)
    {
        var script = plan.BackgroundScript(Environment.ProcessId, SelfUpdatePlan.LogPath);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Not inherited: a caller reading Deployify's output must not wait for the update too.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", SelfUpdatePlan.Encode(script) })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe didn't start");
        process.StandardInput.Close();
    }

    /// <summary>Warns when the AppHost references a Deployify.Hosting package of another version than this CLI.</summary>
    private static void CheckHosting(string projectPath)
    {
        if (FindHostingVersion(projectPath) is not var (csproj, version, setIn))
        {
            return;
        }

        var current = CliVersion.Current;
        if (version.Contains("$(", StringComparison.Ordinal) || version.Contains('*', StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]The AppHost sets {HostingPackage}'s version as {version} in {setIn}; keep it at {current}, this Deployify's version.[/]");
        }
        else if (VersionOrder.Compare(version, current) != 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]![/] The AppHost uses {HostingPackage} {version} (set in {setIn}), but this Deployify is {current}. Keep them on the same version: dotnet add \"{csproj}\" package {HostingPackage} --version {current}");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[green]✓[/] {HostingPackage} {version} matches this Deployify.");
        }
    }

    /// <summary>
    /// The AppHost project and the Deployify.Hosting version it uses: the PackageReference's
    /// VersionOverride or Version, else the PackageVersion in the nearest Directory.Packages.props.
    /// Null when the AppHost can't be found, or doesn't reference the package (a ProjectReference to
    /// Deployify.Hosting, as in Deployify's own samples, isn't a package to update).
    /// </summary>
    internal static (string Csproj, string Version, string SetIn)? FindHostingVersion(string projectPath)
    {
        string? csproj = null;
        if (File.Exists(projectPath) && projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            csproj = Path.GetFullPath(projectPath);
        }
        else if (Directory.Exists(projectPath) && Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly) is [var only])
        {
            csproj = Path.GetFullPath(only);
        }

        if (csproj is null || Load(csproj) is not { } project)
        {
            return null;
        }

        var reference = project.Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageReference" && Names(e, HostingPackage));
        if (reference is null)
        {
            return null;
        }

        if ((Value(reference, "VersionOverride") ?? Value(reference, "Version")) is { } own)
        {
            return (csproj, own, Path.GetFileName(csproj));
        }

        for (var dir = new DirectoryInfo(Path.GetDirectoryName(csproj)!); dir is not null; dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (!File.Exists(props))
            {
                continue;
            }

            var central = Load(props)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "PackageVersion" && Names(e, HostingPackage));
            return central is not null && Value(central, "Version") is { } pinned ? (csproj, pinned, props) : null;
        }

        return null;

        static bool Names(XElement e, string package)
            => string.Equals((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"), package, StringComparison.OrdinalIgnoreCase);

        static string? Value(XElement e, string name)
            => ((string?)e.Attribute(name) ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value)?.Trim() is { Length: > 0 } v ? v : null;

        static XDocument? Load(string path)
        {
            try
            {
                return XDocument.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }
    }
}
