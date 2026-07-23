using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Wraps kubectl CLI calls. Used as primary apply mechanism.
/// Falls back to <see cref="KubernetesApiClient"/> on failure.
/// </summary>
public sealed class KubectlRunner(KubernetesTargetConfig config, ILogger logger)
{
    private readonly string _contextArgs = BuildContextArgs(config);

    /// <summary>Returns true if kubectl is available on PATH.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var result = await RunRawAsync("kubectl", "version --client --output=json", null);
            return result.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Checks connectivity to the configured cluster.</summary>
    public async Task<(bool Ok, string? Error)> PingAsync()
    {
        var result = await RunAsync("cluster-info");
        return result.ExitCode == 0
            ? (true, null)
            : (false, result.Stderr.Trim());
    }

    /// <summary>Applies a YAML file. Returns (success, output).</summary>
    public async Task<(bool Ok, string Output)> ApplyFileAsync(string yamlPath)
    {
        var result = await RunAsync($"apply -f \"{yamlPath}\"");
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Applies all YAML files in a directory recursively.</summary>
    public async Task<(bool Ok, string Output)> ApplyDirectoryAsync(string directory)
    {
        var result = await RunAsync($"apply -R -f \"{directory}\"");
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>Returns the names of all deployments in a namespace.</summary>
    public async Task<IReadOnlyList<string>> GetDeploymentsAsync(string? namespaceName = null)
    {
        var nsArg = namespaceName is not null ? $"-n {namespaceName}" : "--all-namespaces";
        var result = await RunAsync($"get deployments {nsArg} -o jsonpath='{{.items[*].metadata.name}}'");
        if (result.ExitCode != 0)
        {
	        return [];
        }

        return result.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Returns all namespaces matching a prefix.</summary>
    public async Task<IReadOnlyList<string>> GetNamespacesWithPrefixAsync(string prefix)
    {
        var result = await RunAsync(
            $"get namespaces -o jsonpath='{{.items[*].metadata.name}}' " +
            $"--field-selector=metadata.name!={prefix}");

        if (result.ExitCode != 0)
        {
	        return [];
        }

        return result.Stdout
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(n => n.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Deletes a namespace (and everything in it).</summary>
    public async Task<bool> DeleteNamespaceAsync(string namespaceName)
    {
        var result = await RunAsync($"delete namespace {namespaceName} --ignore-not-found");
        return result.ExitCode == 0;
    }

    /// <summary>Gets the current rollout status of a deployment.</summary>
    public async Task<string> GetRolloutStatusAsync(string deploymentName, string namespaceName)
    {
        var result = await RunAsync($"rollout status deployment/{deploymentName} -n {namespaceName} --timeout=5s");
        return result.ExitCode == 0 ? result.Stdout.Trim() : result.Stderr.Trim();
    }

    /// <summary>Diffs a YAML file against the live cluster state.</summary>
    public async Task<string?> DiffFileAsync(string yamlPath)
    {
        var result = await RunAsync($"diff -f \"{yamlPath}\"");
        // exit code 1 = differences found (not an error), exit code > 1 = actual error
        if (result.ExitCode > 1)
        {
	        return null;
        }

        return result.Stdout.Trim();
    }

    /// <summary>Validates a YAML file using dry-run.</summary>
    public async Task<IReadOnlyList<string>> ValidateFileAsync(string yamlPath)
    {
        var result = await RunAsync($"apply -f \"{yamlPath}\" --dry-run=server");
        if (result.ExitCode == 0)
        {
	        return [];
        }

        return result.Stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private Task<ProcessResult> RunAsync(string args) =>
        RunRawAsync("kubectl", $"{_contextArgs} {args}", logger);

    private static async Task<ProcessResult> RunRawAsync(string command, string args, ILogger? log)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{command}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); log?.LogDebug("[kubectl] {Line}", e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); log?.LogDebug("[kubectl stderr] {Line}", e.Data); } };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string BuildContextArgs(KubernetesTargetConfig cfg)
    {
        var parts = new List<string>();
        if (cfg.KubeConfigPath is not null)
        {
	        parts.Add($"--kubeconfig \"{cfg.KubeConfigPath}\"");
        }

        if (cfg.Context is not null)
        {
	        parts.Add($"--context {cfg.Context}");
        }

        return string.Join(" ", parts);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
