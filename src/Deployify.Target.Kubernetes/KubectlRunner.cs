using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Wraps kubectl CLI calls. Used as primary apply mechanism.
/// Falls back to <see cref="KubernetesApiClient"/> on failure.
/// </summary>
public sealed class KubectlRunner(KubernetesTargetConfig config, ILogger logger, IProcessRunner? runner = null)
    : IKubernetesSecretClient
{
    private readonly IReadOnlyList<string> _contextArgs = BuildContextArgs(config);
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    /// <summary>Label every generated resource carries; used to find our namespaces.</summary>
    public const string ManagedBySelector = "app.kubernetes.io/managed-by=deployify";

    /// <summary>Returns true if kubectl is available on PATH.</summary>
    public static Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => IsAvailableAsync(ProcessRunner.Default, ct);

    /// <summary>Returns true if kubectl is available on PATH.</summary>
    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("kubectl", ["version", "--client", "--output=json"], ct: ct);

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

    /// <summary>
    /// Returns the Deployify-managed namespaces whose name starts with the prefix. Throws when
    /// kubectl can't reach the cluster.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetNamespacesWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "get", "namespaces", "-l", ManagedBySelector,
            "-o", "jsonpath={.items[*].metadata.name}");
        if (!result.Ok)
        {
            // Returning an empty list here would make every service look new.
            throw new InvalidOperationException($"kubectl could not list namespaces: {result.Stderr.Trim()}");
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
    /// Diffs a YAML file or directory against the live cluster. kubectl exits with 0 when
    /// nothing differs, 1 when something does, and higher when the diff itself failed.
    /// </summary>
    public async Task<KubectlDiff> DiffFileAsync(string path, CancellationToken ct = default)
    {
        string[] args = Directory.Exists(path) ? ["diff", "-R", "-f", path] : ["diff", "-f", path];
        var result = await RunAsync(ct, args);
        return result.ExitCode switch
        {
            0 => KubectlDiff.Same,
            1 => KubectlDiff.Different(result.Stdout.Trim()),
            _ => KubectlDiff.Failed(DescribeDiffFailure(result.Stderr))
        };
    }

    /// <summary>
    /// kubectl diff shells out to a <c>diff</c> program, which Windows doesn't have unless
    /// Git's usr\bin is on PATH; say so instead of passing on Go's "executable file not found".
    /// </summary>
    internal static string DescribeDiffFailure(string stderr)
    {
        var message = stderr.Trim();
        if (message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("diff", StringComparison.OrdinalIgnoreCase))
        {
            return "kubectl diff needs a 'diff' program on PATH. On Windows, add Git's usr\\bin folder to PATH " +
                   "or set KUBECTL_EXTERNAL_DIFF to a diff tool.";
        }

        return message.Length == 0 ? "kubectl diff failed without an error message." : message.Split('\n')[0].Trim();
    }

    /// <summary>Validates a YAML file using dry-run.</summary>
    public async Task<IReadOnlyList<string>> ValidateFileAsync(string yamlPath, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "apply", "-f", yamlPath, "--dry-run=server");
        if (result.Ok)
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

    // ── Secrets ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, SensitiveValue>?> GetSecretDataAsync(
        string namespaceName, string name, CancellationToken ct = default)
    {
        // Output holds secret data: run without logging it.
        var result = await RunQuietAsync(null, ct, "get", "secret", name, "-n", namespaceName, "-o", "json", "--ignore-not-found");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"kubectl could not read secret {namespaceName}/{name}: {result.Stderr.Trim()}");
        }

        return string.IsNullOrWhiteSpace(result.Stdout) ? null : KubernetesSecretManifest.ReadData(result.Stdout);
    }

    /// <inheritdoc />
    public async Task<string?> GetResourceVersionAsync(string kind, string namespaceName, string name, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "get", kind, name, "-n", namespaceName, "--ignore-not-found",
            "-o", "jsonpath={.metadata.resourceVersion}");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout.Trim() : null;
    }

    /// <inheritdoc />
    public async Task<string> ServerSideApplyAsync(string json, CancellationToken ct = default)
    {
        // The object is sent on stdin, so a Secret never touches the disk. Server-side apply
        // also avoids the last-applied-configuration annotation, which would hold a second
        // copy of every value.
        var result = await RunQuietAsync(json, ct, "apply", "--server-side", "--field-manager=deployify",
            "--force-conflicts", "-f", "-", "-o", "jsonpath={.metadata.resourceVersion}");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"kubectl apply failed: {result.Stderr.Trim()}");
        }

        return result.Stdout.Trim();
    }

    /// <summary>Restarts a deployment's pods so they pick up changed secrets.</summary>
    public async Task<bool> RolloutRestartAsync(string deploymentName, string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "rollout", "restart", $"deployment/{deploymentName}", "-n", namespaceName);
        return result.ExitCode == 0;
    }

    private Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args)
        => _runner.RunAsync(new ProcessSpec("kubectl", [.. _contextArgs, .. args]) { Log = logger, LogTag = "kubectl" }, ct);

    /// <summary>For calls whose input or output holds secret data: nothing is logged.</summary>
    private Task<ProcessResult> RunQuietAsync(string? stdin, CancellationToken ct, params string[] args)
        => _runner.RunAsync(new ProcessSpec("kubectl", [.. _contextArgs, .. args]) { Stdin = stdin, Sensitive = true }, ct);

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
}

/// <summary>The outcome of <c>kubectl diff</c>: the same, different (with the diff), or unknown.</summary>
public sealed record KubectlDiff
{
    private KubectlDiff(KubectlDiffKind kind, string text) => (Kind, Text) = (kind, text);

    public KubectlDiffKind Kind { get; }

    /// <summary>The diff when different, the reason when failed, empty when the same.</summary>
    public string Text { get; }

    public static KubectlDiff Same { get; } = new(KubectlDiffKind.Same, "");

    public static KubectlDiff Different(string diff) => new(KubectlDiffKind.Different, diff);

    public static KubectlDiff Failed(string reason) => new(KubectlDiffKind.Failed, reason);
}

public enum KubectlDiffKind
{
    Same,
    Different,
    Failed
}
