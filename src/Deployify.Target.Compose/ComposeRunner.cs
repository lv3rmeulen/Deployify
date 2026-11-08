using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Compose;

/// <summary>
/// Wraps the Docker Compose CLI.
/// Tries "docker compose" (plugin) first, falls back to "docker-compose" (standalone).
/// </summary>
public sealed class ComposeRunner(ComposeTargetConfig config, ILogger logger, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;
    private string? _resolvedBinary;
    private bool? _usePlugin;

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>Returns true if any compose CLI is available on PATH.</summary>
    public static Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => IsAvailableAsync(ProcessRunner.Default, ct);

    /// <summary>Returns true if any compose CLI is available on PATH.</summary>
    public static async Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => await DetectCliAsync(runner, ct) is not null;

    /// <summary>Returns a description of which CLI will be used, or null if none found.</summary>
    public static Task<string?> DetectCliAsync(CancellationToken ct = default)
        => DetectCliAsync(ProcessRunner.Default, ct);

    /// <summary>Returns a description of which CLI will be used, or null if none found.</summary>
    public static async Task<string?> DetectCliAsync(IProcessRunner runner, CancellationToken ct = default)
    {
        if (await runner.IsAvailableAsync("docker", ["compose", "version"], ct: ct))
        {
            return "docker compose (plugin)";
        }

        if (await runner.IsAvailableAsync("docker-compose", ["--version"], ct: ct))
        {
            return "docker-compose (standalone)";
        }

        return null;
    }

    // ── Core operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks the Docker daemon is reachable. Uses <c>docker info</c>: Compose itself has
    /// no <c>info</c> subcommand, so the old <c>docker compose info</c> always failed.
    /// </summary>
    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var result = await RunDockerAsync(["info", "--format", "{{.ServerVersion}}"], ct);
        return result.ExitCode == 0
            ? (true, null)
            : (false, $"Docker daemon unreachable: {result.Stderr.Trim()}");
    }

    /// <summary>
    /// Runs "docker compose up -d" using the merged compose file,
    /// optionally scoped to specific profiles.
    /// </summary>
    public async Task<(bool Ok, string Output)> UpAsync(
        string composeFilePath,
        IEnumerable<string>? profiles = null,
        bool forceRecreate = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "-p", config.ProjectName };
        args.AddRange(ProfileArgs(profiles));
        args.AddRange(["-f", composeFilePath, "up", "-d"]);
        if (forceRecreate)
        {
            args.Add("--force-recreate");
        }

        args.Add("--remove-orphans");
        var result = await RunAsync(args, ct);
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Brings down the project, removing containers (but not volumes).</summary>
    public async Task<(bool Ok, string Output)> DownAsync(
        string composeFilePath, bool removeVolumes = false, CancellationToken ct = default)
    {
        var args = new List<string> { "-p", config.ProjectName, "-f", composeFilePath, "down" };
        if (removeVolumes)
        {
            args.Add("-v");
        }

        var result = await RunAsync(args, ct);
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Returns running service names under the project.</summary>
    public async Task<IReadOnlyList<string>> GetRunningServicesAsync(
        string composeFilePath, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["-p", config.ProjectName, "-f", composeFilePath, "ps", "--services", "--filter", "status=running"], ct);
        if (result.ExitCode != 0)
        {
            return [];
        }

        return
        [
            .. result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
        ];
    }

    /// <summary>Validates compose files using "config" (dry-run parse).</summary>
    public async Task<(bool Ok, string Output)> ValidateAsync(string composeFilePath, CancellationToken ct = default)
    {
        var result = await RunAsync(["-f", composeFilePath, "config", "--quiet"], ct);
        return (result.ExitCode == 0, result.ExitCode == 0 ? "Valid" : result.Stderr.Trim());
    }

    /// <summary>
    /// Returns a human-readable list of what "up" would change.
    /// Uses "docker compose up --dry-run" (available in Compose v2.18+).
    /// Returns null when dry-run is not supported.
    /// </summary>
    public async Task<string?> DryRunUpAsync(
        string composeFilePath, IEnumerable<string>? profiles = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-p", config.ProjectName };
        args.AddRange(ProfileArgs(profiles));
        args.AddRange(["-f", composeFilePath, "up", "-d", "--dry-run"]);
        var result = await RunAsync(args, ct);
        if (result.ExitCode == 0 || result.Stdout.Contains("DRY-RUN", StringComparison.OrdinalIgnoreCase))
        {
            return result.Stdout.Trim() + result.Stderr.Trim();
        }

        return null;
    }

    /// <summary>Creates the shared project network if it doesn't exist.</summary>
    public async Task EnsureNetworkAsync(CancellationToken ct = default)
    {
        var check = await RunDockerAsync(["network", "inspect", config.ProjectName], ct);
        if (check.Ok)
        {
            return;
        }

        await RunDockerAsync(["network", "create", config.ProjectName], ct);
    }

    /// <summary>Creates any declared named volumes if they don't exist.</summary>
    public async Task EnsureVolumesAsync(IEnumerable<string> volumeNames, CancellationToken ct = default)
    {
        foreach (var vol in volumeNames)
        {
            var check = await RunDockerAsync(["volume", "inspect", vol], ct);
            if (!check.Ok)
            {
                await RunDockerAsync(["volume", "create", vol], ct);
            }
        }
    }

    // ── Rollout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The project's containers for one service, running or not. Throws when the Docker daemon
    /// can't be asked, so "unreachable" is never taken for "new service".
    /// </summary>
    public async Task<IReadOnlyList<ComposeContainerStatus>> GetServiceContainersAsync(
        string service, CancellationToken ct = default)
    {
        var ps = await RunDockerAsync(
        [
            "ps", "-a", "-q",
            "--filter", $"label=com.docker.compose.project={config.ProjectName}",
            "--filter", $"label=com.docker.compose.service={service}"
        ], ct);
        if (!ps.Ok)
        {
            throw new InvalidOperationException($"docker could not list the containers of '{service}': {ps.Stderr.Trim()}");
        }

        var ids = ps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var inspect = await RunDockerAsync(["inspect", .. ids], ct);
        if (!inspect.Ok)
        {
            throw new InvalidOperationException($"docker could not inspect the containers of '{service}': {inspect.Stderr.Trim()}");
        }

        return ComposeContainerStatus.ParseInspect(inspect.Stdout);
    }

    /// <summary>
    /// Brings up one service from the given compose files without touching its dependencies
    /// (<c>up -d --no-deps service</c>).
    /// </summary>
    public async Task<(bool Ok, string Output)> UpServiceAsync(
        IReadOnlyList<string> composeFiles, string service, IEnumerable<string>? profiles = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "-p", config.ProjectName };
        args.AddRange(ProfileArgs(profiles));
        foreach (var file in composeFiles)
        {
            args.AddRange(["-f", file]);
        }

        args.AddRange(["up", "-d", "--no-deps", service]);
        var result = await RunAsync(args, ct);
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Force-removes containers (stopping them first).</summary>
    public async Task RemoveContainersAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var result = await RunDockerAsync(["rm", "-f", .. list], ct);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"docker could not remove containers: {result.Stderr.Trim()}");
        }
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>
    /// Names of the services that have containers in this project, running or not. Throws when
    /// the Docker daemon can't be asked.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetProjectServicesAsync(CancellationToken ct = default)
    {
        var ps = await RunDockerAsync(
        [
            "ps", "-a", "--filter", $"label=com.docker.compose.project={config.ProjectName}",
            "--format", "{{.Label \"com.docker.compose.service\"}}"
        ], ct);
        if (!ps.Ok)
        {
            throw new InvalidOperationException($"docker could not list the containers of project '{config.ProjectName}': {ps.Stderr.Trim()}");
        }

        return
        [
            .. ps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Removes the project's network (when it exists) and, when asked, its volumes. Returns what it removed.</summary>
    public async Task<IReadOnlyList<string>> RemoveProjectResourcesAsync(
        IEnumerable<string> declaredVolumes, bool removeVolumes, CancellationToken ct = default)
    {
        var removed = new List<string>();
        if ((await RunDockerAsync(["network", "inspect", config.ProjectName], ct)).Ok)
        {
            var rm = await RunDockerAsync(["network", "rm", config.ProjectName], ct);
            if (!rm.Ok)
            {
                throw new InvalidOperationException($"docker could not remove network {config.ProjectName}: {rm.Stderr.Trim()}");
            }

            removed.Add($"network {config.ProjectName}");
        }

        if (!removeVolumes)
        {
            return removed;
        }

        var ls = await RunDockerAsync(["volume", "ls", "-q", "--filter", $"label=com.docker.compose.project={config.ProjectName}"], ct);
        if (!ls.Ok)
        {
            throw new InvalidOperationException($"docker could not list volumes: {ls.Stderr.Trim()}");
        }

        var volumes = ls.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(declaredVolumes)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var volume in volumes)
        {
            if (!(await RunDockerAsync(["volume", "inspect", volume], ct)).Ok)
            {
                continue;
            }

            var rm = await RunDockerAsync(["volume", "rm", volume], ct);
            if (!rm.Ok)
            {
                throw new InvalidOperationException($"docker could not remove volume {volume}: {rm.Stderr.Trim()}");
            }

            removed.Add($"volume {volume}");
        }

        return removed;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ProcessResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        await ResolveCliAsync(ct);
        return _usePlugin == true
            ? await _runner.RunAsync(new ProcessSpec("docker", ["compose", .. args]) { Log = logger, LogTag = "compose" }, ct)
            : await _runner.RunAsync(new ProcessSpec("docker-compose", [.. args]) { Log = logger, LogTag = "compose" }, ct);
    }

    private Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> args, CancellationToken ct)
        => _runner.RunAsync(new ProcessSpec("docker", args) { Log = logger }, ct);

    private async Task ResolveCliAsync(CancellationToken ct)
    {
        if (_resolvedBinary is not null)
        {
            return;
        }

        if (await _runner.IsAvailableAsync("docker", ["compose", "version"], ct: ct))
        {
            _resolvedBinary = "docker";
            _usePlugin = true;
            logger.LogDebug("Using Docker Compose plugin (docker compose).");
        }
        else if (await _runner.IsAvailableAsync("docker-compose", ["--version"], ct: ct))
        {
            _resolvedBinary = "docker-compose";
            _usePlugin = false;
            logger.LogDebug("Using Docker Compose standalone (docker-compose).");
        }
        else
        {
            throw new InvalidOperationException(
                "Neither 'docker compose' nor 'docker-compose' found on PATH. " +
                "Install Docker Desktop or the Compose plugin.");
        }
    }

    private static IEnumerable<string> ProfileArgs(IEnumerable<string>? profiles)
        => (profiles ?? []).SelectMany(p => new[] { "--profile", p });
}
