using System.Diagnostics;
using System.Text;
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
    public static async Task<bool> IsAvailableAsync()
    {
        if (await TryBinaryAsync("docker", "compose version"))
        {
	        return true;
        }

        if (await TryBinaryAsync("docker-compose", "--version"))
        {
	        return true;
        }

        return false;
    }

    /// <summary>Returns a description of which CLI will be used, or null if none found.</summary>
    public static async Task<string?> DetectCliAsync()
    {
        if (await TryBinaryAsync("docker", "compose version"))
        {
	        return "docker compose (plugin)";
        }

        if (await TryBinaryAsync("docker-compose", "--version"))
        {
	        return "docker-compose (standalone)";
        }

        return null;
    }

    // ── Core operations ───────────────────────────────────────────────────────

    /// <summary>Checks the Docker daemon is reachable.</summary>
    public async Task<(bool Ok, string? Error)> PingAsync()
    {
        var result = await RunAsync("info --format '{{.ServerVersion}}'");
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
        bool forceRecreate = false)
    {
        var profileArgs = BuildProfileArgs(profiles);
        var recreateFlag = forceRecreate ? " --force-recreate" : "";
        var result = await RunAsync(
            $"-p {config.ProjectName} {profileArgs} -f \"{composeFilePath}\" up -d{recreateFlag} --remove-orphans");
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Brings down the project, removing containers (but not volumes).</summary>
    public async Task<(bool Ok, string Output)> DownAsync(string composeFilePath, bool removeVolumes = false)
    {
        var volFlag = removeVolumes ? " -v" : "";
        var result = await RunAsync(
            $"-p {config.ProjectName} -f \"{composeFilePath}\" down{volFlag}");
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Returns running service names under the project.</summary>
    public async Task<IReadOnlyList<string>> GetRunningServicesAsync(string composeFilePath)
    {
        var result = await RunAsync(
            $"-p {config.ProjectName} -f \"{composeFilePath}\" ps --services --filter status=running");
        if (result.ExitCode != 0)
        {
	        return [];
        }

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>Validates compose files using "config" (dry-run parse).</summary>
    public async Task<(bool Ok, string Output)> ValidateAsync(string composeFilePath)
    {
        var result = await RunAsync($"-f \"{composeFilePath}\" config --quiet");
        return (result.ExitCode == 0, result.ExitCode == 0 ? "Valid" : result.Stderr.Trim());
    }

    /// <summary>
    /// Returns a human-readable diff of what "up" would change.
    /// Uses "docker compose up --dry-run" (available in Compose v2.18+).
    /// Falls back to comparing running vs desired service names.
    /// </summary>
    public async Task<string?> DryRunUpAsync(string composeFilePath, IEnumerable<string>? profiles = null)
    {
        var profileArgs = BuildProfileArgs(profiles);
        var result = await RunAsync(
            $"-p {config.ProjectName} {profileArgs} -f \"{composeFilePath}\" up -d --dry-run");
        if (result.ExitCode == 0 || result.Stdout.Contains("DRY-RUN", StringComparison.OrdinalIgnoreCase))
        {
	        return result.Stdout.Trim();
        }

        return null; // dry-run not supported in this version
    }

    /// <summary>Creates the shared project network if it doesn't exist.</summary>
    public async Task EnsureNetworkAsync()
    {
        var check = await RunDockerAsync($"network inspect {config.ProjectName}");
        if (check.ExitCode == 0)
        {
	        return; // already exists
        }

        await RunDockerAsync($"network create {config.ProjectName}");
    }

    /// <summary>Creates any declared named volumes if they don't exist.</summary>
    public async Task EnsureVolumesAsync(IEnumerable<string> volumeNames)
    {
        foreach (var vol in volumeNames)
        {
            var check = await RunDockerAsync($"volume inspect {vol}");
            if (check.ExitCode != 0)
            {
	            await RunDockerAsync($"volume create {vol}");
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ProcessResult> RunAsync(string args)
    {
        await ResolveCliAsync();

        return _usePlugin == true
            ? await RunRawAsync("docker", $"compose {args}", logger)
            : await RunRawAsync("docker-compose", args, logger);
    }

    private static Task<ProcessResult> RunDockerAsync(string args) =>
        RunRawAsync("docker", args, null);

    private async Task ResolveCliAsync()
    {
        if (_resolvedBinary is not null)
        {
	        return;
        }

        if (await TryBinaryAsync("docker", "compose version"))
        {
            _resolvedBinary = "docker";
            _usePlugin = true;
            logger.LogDebug("Using Docker Compose plugin (docker compose).");
        }
        else if (await TryBinaryAsync("docker-compose", "--version"))
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

    private static async Task<bool> TryBinaryAsync(string binary, string args)
    {
        try
        {
            var result = await RunRawAsync(binary, args, null);
            return result.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<ProcessResult> RunRawAsync(string binary, string args, ILogger? log)
    {
        var psi = new ProcessStartInfo(binary, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

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

            stdout.AppendLine(e.Data);
            log?.LogDebug("[compose] {Line}", e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
	            return;
            }

            stderr.AppendLine(e.Data);
            log?.LogDebug("[compose stderr] {Line}", e.Data);
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string BuildProfileArgs(IEnumerable<string>? profiles)
    {
        var list = profiles?.ToList();
        if (list is null || list.Count == 0)
        {
	        return string.Empty;
        }

        return string.Join(" ", list.Select(p => $"--profile {p}"));
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
