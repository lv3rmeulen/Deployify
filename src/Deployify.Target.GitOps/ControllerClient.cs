using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.GitOps;

/// <summary>
/// The few kubectl calls the GitOps targets make against the controller's own objects
/// (Applications, Kustomizations, GitRepositories): apply, read, annotate and delete.
/// </summary>
public sealed class ControllerClient(KubernetesTargetConfig config, ILogger logger, IProcessRunner? runner = null)
{
    private readonly IReadOnlyList<string> _contextArgs = BuildContextArgs(config);
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    /// <summary>True when the custom resource is installed in the cluster (the controller is there).</summary>
    public async Task<bool> HasCrdAsync(string crdName, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "get", "crd", crdName, "--ignore-not-found", "-o", "name");
        if (!result.Ok)
        {
            throw new InvalidOperationException($"kubectl could not read CRD {crdName}: {result.ErrorOrOutput.Trim()}");
        }

        return !string.IsNullOrWhiteSpace(result.Stdout);
    }

    /// <summary>Builds a Kustomize folder (<c>kubectl kustomize</c>), offline; returns its errors.</summary>
    public async Task<IReadOnlyList<string>> BuildKustomizationAsync(string directory, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new ProcessSpec("kubectl", ["kustomize", directory]) { Log = logger, LogTag = "kubectl" }, ct);
        return result.Ok ? [] : [result.ErrorOrOutput.Trim()];
    }

    /// <summary>Applies a manifest file; throws with kubectl's error when it fails.</summary>
    public async Task ApplyFileAsync(string path, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "apply", "-f", path);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"kubectl apply -f {path} failed: {result.ErrorOrOutput.Trim()}");
        }
    }

    /// <summary>The object as JSON, or null when it doesn't exist. Throws when the cluster can't be asked.</summary>
    public async Task<string?> GetJsonAsync(string resource, string namespaceName, string name, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "get", resource, name, "-n", namespaceName, "-o", "json", "--ignore-not-found");
        if (!result.Ok)
        {
            throw new InvalidOperationException($"kubectl could not read {resource} {namespaceName}/{name}: {result.ErrorOrOutput.Trim()}");
        }

        return string.IsNullOrWhiteSpace(result.Stdout) ? null : result.Stdout;
    }

    /// <summary>Sets an annotation; returns false (without throwing) when it couldn't, for best-effort nudges.</summary>
    public async Task<bool> AnnotateAsync(
        string resource, string namespaceName, string name, string key, string value, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "annotate", resource, name, "-n", namespaceName, $"{key}={value}", "--overwrite");
        return result.Ok;
    }

    /// <summary>
    /// Deletes the object and waits for its finalizers, which is when Argo CD and Flux have
    /// removed what it deployed. One that isn't there is not an error.
    /// </summary>
    public async Task DeleteAsync(
        string resource, string namespaceName, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "delete", resource, name, "-n", namespaceName, "--ignore-not-found", "--wait=true",
            $"--timeout={((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s");
        if (!result.Ok)
        {
            throw new InvalidOperationException($"kubectl could not delete {resource} {namespaceName}/{name}: {result.ErrorOrOutput.Trim()}");
        }
    }

    private Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args)
        => _runner.RunAsync(new ProcessSpec("kubectl", [.. _contextArgs, .. args]) { Log = logger, LogTag = "kubectl" }, ct);

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
