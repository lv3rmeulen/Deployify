using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Wraps kubectl CLI calls. Used as primary apply mechanism.
/// Falls back to <see cref="KubernetesApiClient"/> on failure.
/// </summary>
public sealed class KubectlRunner(KubernetesTargetConfig config, ILogger logger)
{
    private readonly IReadOnlyList<string> _contextArgs = BuildContextArgs(config);

    /// <summary>Label every generated resource carries; used to find our namespaces.</summary>
    public const string ManagedBySelector = "app.kubernetes.io/managed-by=deployify";

    /// <summary>Returns true if kubectl is available on PATH.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunRawAsync("kubectl", ["version", "--client", "--output=json"], null, ct);
            return result.ExitCode == 0;
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

    /// <summary>Checks connectivity to the configured cluster.</summary>
    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "cluster-info");
        return result.ExitCode == 0 ? (true, null) : (false, result.Stderr.Trim());
    }

    /// <summary>Applies a YAML file. Returns (success, output).</summary>
    public async Task<(bool Ok, string Output)> ApplyFileAsync(string yamlPath, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "apply", "-f", yamlPath);
        return (result.ExitCode == 0, result.ExitCode == 0 ? result.Stdout : result.Stderr);
    }

    /// <summary>
    /// Applies all YAML files in a directory. The namespace file goes first: applying a
    /// directory in one call sends files alphabetically, so on an empty cluster the
    /// ConfigMap and Deployment would be rejected because their namespace doesn't exist yet.
    /// </summary>
    public async Task<(bool Ok, string Output)> ApplyDirectoryAsync(string directory, CancellationToken ct = default)
    {
        var output = new StringBuilder();
        var nsFile = Path.Combine(directory, "namespace.yaml");
        if (File.Exists(nsFile))
        {
            var ns = await RunAsync(ct, "apply", "-f", nsFile);
            if (ns.ExitCode != 0)
            {
                return (false, ns.Stderr);
            }

            output.Append(ns.Stdout);
        }

        var result = await RunAsync(ct, "apply", "-R", "-f", directory);
        output.Append(result.ExitCode == 0 ? result.Stdout : result.Stderr);
        return (result.ExitCode == 0, output.ToString());
    }

    /// <summary>Returns the names of all deployments in a namespace.</summary>
    public async Task<IReadOnlyList<string>> GetDeploymentsAsync(string? namespaceName = null, CancellationToken ct = default)
    {
        string[] nsArgs = namespaceName is not null ? ["-n", namespaceName] : ["--all-namespaces"];
        // No shell is involved, so the jsonpath must not be wrapped in quotes — they would
        // end up in kubectl's output and break every name comparison.
        var result = await RunAsync(ct, ["get", "deployments", .. nsArgs, "-o", "jsonpath={.items[*].metadata.name}"]);
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Returns the Deployify-managed namespaces whose name starts with the prefix.</summary>
    public async Task<IReadOnlyList<string>> GetNamespacesWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "get", "namespaces", "-l", ManagedBySelector,
            "-o", "jsonpath={.items[*].metadata.name}");
        if (result.ExitCode != 0)
        {
            return [];
        }

        return
        [
            .. result.Stdout
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => n.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>Deletes a namespace (and everything in it).</summary>
    public async Task<bool> DeleteNamespaceAsync(string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "delete", "namespace", namespaceName, "--ignore-not-found");
        return result.ExitCode == 0;
    }

    /// <summary>Gets the current rollout status of a deployment.</summary>
    public async Task<string> GetRolloutStatusAsync(string deploymentName, string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "rollout", "status", $"deployment/{deploymentName}", "-n", namespaceName, "--timeout=5s");
        return result.ExitCode == 0 ? result.Stdout.Trim() : result.Stderr.Trim();
    }

    /// <summary>
    /// Diffs a YAML file or directory against the live cluster. Returns "" when there are
    /// no differences, the diff text when there are, or null when kubectl failed.
    /// </summary>
    public async Task<string?> DiffFileAsync(string path, CancellationToken ct = default)
    {
        string[] args = Directory.Exists(path) ? ["diff", "-R", "-f", path] : ["diff", "-f", path];
        var result = await RunAsync(ct, args);
        // exit code 1 = differences found (not an error), exit code > 1 = actual error
        if (result.ExitCode > 1)
        {
            return null;
        }

        return result.Stdout.Trim();
    }

    /// <summary>Validates a YAML file using dry-run.</summary>
    public async Task<IReadOnlyList<string>> ValidateFileAsync(string yamlPath, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "apply", "-f", yamlPath, "--dry-run=server");
        if (result.ExitCode == 0)
        {
            return [];
        }

        return
        [
            .. result.Stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
        ];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args)
        => RunRawAsync("kubectl", [.. _contextArgs, .. args], logger, ct);

    /// <summary>
    /// Runs a process with an argument list (no shell, so paths and values with spaces or
    /// quotes are passed through intact) and kills it if the caller cancels.
    /// </summary>
    private static async Task<ProcessResult> RunRawAsync(
        string command, IEnumerable<string> args, ILogger? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command)
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
            ?? throw new InvalidOperationException($"Could not start '{command}'.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }

                log?.LogDebug("[kubectl] {Line}", e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }

                log?.LogDebug("[kubectl stderr] {Line}", e.Data);
            }
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

    private static IReadOnlyList<string> BuildContextArgs(KubernetesTargetConfig cfg)
    {
        var parts = new List<string>();
        if (cfg.KubeConfigPath is not null)
        {
            parts.AddRange(["--kubeconfig", cfg.KubeConfigPath]);
        }

        if (cfg.Context is not null)
        {
            parts.AddRange(["--context", cfg.Context]);
        }

        return parts;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
