using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Compose;

/// <summary>
/// Wraps the Docker Compose CLI.
/// Tries "docker compose" (plugin) first, falls back to "docker-compose" (standalone).
/// </summary>
public sealed class ComposeRunner(ComposeTargetConfig config, ILogger logger)
{
    private string? _resolvedBinary;
    private bool? _usePlugin;

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>Returns true if any compose CLI is available on PATH.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (await TryBinaryAsync("docker", ["compose", "version"], ct))
        {
            return true;
        }

        if (await TryBinaryAsync("docker-compose", ["--version"], ct))
        {
            return true;
        }

        return false;
    }

    /// <summary>Returns a description of which CLI will be used, or null if none found.</summary>
    public static async Task<string?> DetectCliAsync(CancellationToken ct = default)
    {
        if (await TryBinaryAsync("docker", ["compose", "version"], ct))
        {
            return "docker compose (plugin)";
        }

        if (await TryBinaryAsync("docker-compose", ["--version"], ct))
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
        var result = await RunRawAsync("docker", ["info", "--format", "{{.ServerVersion}}"], logger, ct);
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
        var check = await RunRawAsync("docker", ["network", "inspect", config.ProjectName], null, ct);
        if (check.ExitCode == 0)
        {
            return;
        }

        await RunRawAsync("docker", ["network", "create", config.ProjectName], null, ct);
    }

    /// <summary>Creates any declared named volumes if they don't exist.</summary>
    public async Task EnsureVolumesAsync(IEnumerable<string> volumeNames, CancellationToken ct = default)
    {
        foreach (var vol in volumeNames)
        {
            var check = await RunRawAsync("docker", ["volume", "inspect", vol], null, ct);
            if (check.ExitCode != 0)
            {
                await RunRawAsync("docker", ["volume", "create", vol], null, ct);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ProcessResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        await ResolveCliAsync(ct);
        return _usePlugin == true
            ? await RunRawAsync("docker", ["compose", .. args], logger, ct)
            : await RunRawAsync("docker-compose", args, logger, ct);
    }

    private async Task ResolveCliAsync(CancellationToken ct)
    {
        if (_resolvedBinary is not null)
        {
            return;
        }

        if (await TryBinaryAsync("docker", ["compose", "version"], ct))
        {
            _resolvedBinary = "docker";
            _usePlugin = true;
            logger.LogDebug("Using Docker Compose plugin (docker compose).");
        }
        else if (await TryBinaryAsync("docker-compose", ["--version"], ct))
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

    private static async Task<bool> TryBinaryAsync(string binary, string[] args, CancellationToken ct)
    {
        try
        {
            return (await RunRawAsync(binary, args, null, ct)).ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a process with an argument list (no shell quoting involved, so project names
    /// and paths with spaces pass through intact) and kills it if the caller cancels.
    /// </summary>
    private static async Task<ProcessResult> RunRawAsync(
        string binary, IEnumerable<string> args, ILogger? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{binary}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            log?.LogDebug("[compose] {Line}", e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }

            log?.LogDebug("[compose stderr] {Line}", e.Data);
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already exited */
            }

            throw;
        }

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static IEnumerable<string> ProfileArgs(IEnumerable<string>? profiles)
        => (profiles ?? []).SelectMany(p => new[] { "--profile", p });

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
