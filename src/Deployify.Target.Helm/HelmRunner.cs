using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Helm;

/// <summary>Wraps the helm CLI. Every call carries the kubeconfig and context of the environment.</summary>
public sealed class HelmRunner(KubernetesTargetConfig kubernetes, HelmTargetConfig helm, ILogger logger, IProcessRunner? runner = null)
{
    private readonly IReadOnlyList<string> _contextArgs = BuildContextArgs(kubernetes);
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    /// <summary>True when helm is on PATH.</summary>
    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("helm", ["version", "--short"], ct: ct);

    /// <summary>
    /// Installs the chart as <paramref name="release"/>, or upgrades the release when it exists.
    /// Output isn't logged: an inline-secret chart's errors can quote the Secret.
    /// </summary>
    public async Task<ProcessResult> UpgradeInstallAsync(string release, string chartDirectory, string namespaceName, CancellationToken ct = default)
    {
        List<string> args =
        [
            "upgrade", "--install", release, chartDirectory, "--namespace", namespaceName,
            "--history-max", helm.HistoryMax.ToString(CultureInfo.InvariantCulture)
        ];
        if (helm.Wait)
        {
            args.AddRange(["--wait", "--timeout", helm.Timeout]);
        }

        return await RunAsync(args, ct, sensitive: true);
    }

    /// <summary>The release's latest revision and status, or null when there is no such release.</summary>
    public async Task<HelmRelease?> StatusAsync(string release, string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(["status", release, "--namespace", namespaceName, "--output", "json"], ct, sensitive: true);
        if (!result.Ok)
        {
            if (IsNotFound(result.Stderr))
            {
                return null;
            }

            throw new InvalidOperationException($"helm could not read release {namespaceName}/{release}: {result.ErrorOrOutput.Trim()}");
        }

        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        var status = root.TryGetProperty("info", out var info) && info.TryGetProperty("status", out var s) ? s.GetString() : null;
        return new HelmRelease(
            release, namespaceName,
            root.TryGetProperty("version", out var v) && v.TryGetInt32(out var revision) ? revision : 0,
            status ?? "unknown");
    }

    /// <summary>Every release in every namespace, in any state. Throws when helm can't reach the cluster.</summary>
    public async Task<IReadOnlyList<HelmRelease>> ListAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["list", "--all-namespaces", "--all", "--output", "json"], ct);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"helm could not list releases: {result.ErrorOrOutput.Trim()}");
        }

        return ParseList(result.Stdout);
    }

    internal static IReadOnlyList<HelmRelease> ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. doc.RootElement.EnumerateArray().Select(r => new HelmRelease(
                Str(r, "name") ?? "",
                Str(r, "namespace") ?? "",
                int.TryParse(Str(r, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev) ? rev : 0,
                Str(r, "status") ?? "unknown"))
        ];

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.GetString() : null;
    }

    /// <summary>Rolls the release back to <paramref name="revision"/> (a new revision is recorded).</summary>
    public async Task RollbackAsync(string release, string namespaceName, int revision, CancellationToken ct = default)
    {
        var result = await RunAsync(
        [
            "rollback", release, revision.ToString(CultureInfo.InvariantCulture), "--namespace", namespaceName,
            "--history-max", helm.HistoryMax.ToString(CultureInfo.InvariantCulture)
        ], ct, sensitive: true);
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                $"helm could not roll {namespaceName}/{release} back to revision {revision}: {result.ErrorOrOutput.Trim()}");
        }
    }

    /// <summary>Uninstalls the release. A release that isn't there is not an error.</summary>
    public async Task UninstallAsync(string release, string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(["uninstall", release, "--namespace", namespaceName], ct);
        if (!result.Ok && !IsNotFound(result.Stderr))
        {
            throw new InvalidOperationException($"helm could not uninstall {namespaceName}/{release}: {result.ErrorOrOutput.Trim()}");
        }
    }

    /// <summary>
    /// Compares the chart with the installed release using the helm-diff plugin. Without the
    /// plugin the result is Failed, with how to install it. helm-diff hides Secret values.
    /// </summary>
    public async Task<KubectlDiff> DiffAsync(string release, string chartDirectory, string namespaceName, CancellationToken ct = default)
    {
        var result = await RunAsync(
        [
            "diff", "upgrade", release, chartDirectory, "--namespace", namespaceName,
            "--allow-unreleased", "--detailed-exitcode", "--no-color"
        ], ct, sensitive: true);
        return result.ExitCode switch
        {
            0 => KubectlDiff.Same,
            2 => KubectlDiff.Different(result.Stdout.Trim()),
            _ => KubectlDiff.Failed(DescribeDiffFailure(result.ErrorOrOutput))
        };
    }

    internal static string DescribeDiffFailure(string output)
    {
        var message = output.Trim();
        if (message.Contains("unknown command \"diff\"", StringComparison.OrdinalIgnoreCase))
        {
            return "comparing needs the helm-diff plugin: helm plugin install https://github.com/databus23/helm-diff";
        }

        return message.Length == 0 ? "helm diff failed without an error message." : message.Split('\n')[0].Trim();
    }

    /// <summary>Runs <c>helm lint</c>; returns its error lines (none when the chart is fine).</summary>
    public async Task<IReadOnlyList<string>> LintAsync(string chartDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(["lint", chartDirectory], ct, sensitive: true);
        if (result.Ok)
        {
            return [];
        }

        var errors = (result.Stdout + "\n" + result.Stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("[ERROR]", StringComparison.Ordinal) || l.StartsWith("Error:", StringComparison.Ordinal))
            .ToList();
        return errors.Count > 0 ? errors : [result.ErrorOrOutput.Trim()];
    }

    private static bool IsNotFound(string stderr)
        => stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool sensitive = false)
        => _runner.RunAsync(new ProcessSpec("helm", [.. _contextArgs, .. args])
        {
            Log = sensitive ? null : logger,
            LogTag = "helm",
            Sensitive = sensitive
        }, ct);

    private static IReadOnlyList<string> BuildContextArgs(KubernetesTargetConfig cfg)
    {
        var parts = new List<string>();
        if (cfg.KubeConfigPath is not null)
        {
            parts.AddRange(["--kubeconfig", cfg.KubeConfigPath]);
        }

        if (cfg.Context is not null)
        {
            parts.AddRange(["--kube-context", cfg.Context]);
        }

        return parts;
    }
}

/// <summary>A Helm release: its latest revision and that revision's status (deployed, failed, …).</summary>
public sealed record HelmRelease(string Name, string Namespace, int Revision, string Status);
