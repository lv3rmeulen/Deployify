using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;

namespace Deployify.Core.State;

/// <summary>
/// One Kubernetes Secret per key, in a namespace of its own, through kubectl. Secrets rather
/// than ConfigMaps because state holds generated parameter values. The version tag is the
/// Secret's resourceVersion: a create fails when it exists, and a replace that quotes a stale
/// resourceVersion is rejected by the API server.
/// </summary>
public sealed class KubernetesStateBackend(KubernetesStateConfig config, IProcessRunner? processes = null) : IStateBackend
{
    public const string StateLabel = "deployify.io/state";
    public const string KeyAnnotation = "deployify.io/key";

    private readonly IProcessRunner _processes = processes ?? ProcessRunner.Default;

    public string Description => $"kubernetes secrets in {config.Namespace}";

    /// <summary>The Secret name for a key: names can't hold slashes, so a hash; the key itself is an annotation.</summary>
    public static string SecretName(string key)
        => "deployify-state-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();

    public async Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default)
    {
        var result = await RunAsync(["get", "secret", SecretName(key), "-o", "json", "--ignore-not-found"], ct, sensitive: true);
        if (!result.Ok)
        {
            throw Fail("read", key, result);
        }

        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        var secret = JsonNode.Parse(result.Stdout)!;
        var value = secret["data"]?["value"]?.GetValue<string>();
        return new StateBlob(value is null ? [] : Convert.FromBase64String(value),
            secret["metadata"]!["resourceVersion"]!.GetValue<string>());
    }

    public async Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default)
    {
        var metadata = new JsonObject
        {
            ["name"] = SecretName(key),
            ["namespace"] = config.Namespace,
            ["labels"] = new JsonObject { [StateLabel] = "true", ["app.kubernetes.io/managed-by"] = "deployify" },
            ["annotations"] = new JsonObject { [KeyAnnotation] = key }
        };
        if (expectedETag is not null)
        {
            metadata["resourceVersion"] = expectedETag;
        }

        var body = new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = metadata,
            ["type"] = "Opaque",
            ["data"] = new JsonObject { ["value"] = Convert.ToBase64String(data) }
        }.ToJsonString();

        var result = await RunAsync([expectedETag is null ? "create" : "replace", "-f", "-", "-o", "json"], ct,
            sensitive: true, stdin: body);
        if (!result.Ok)
        {
            var err = result.ErrorOrOutput;
            if (err.Contains("AlreadyExists", StringComparison.Ordinal) || err.Contains("already exists", StringComparison.Ordinal) ||
                err.Contains("Conflict", StringComparison.Ordinal) || err.Contains("has been modified", StringComparison.Ordinal) ||
                (expectedETag is not null && err.Contains("NotFound", StringComparison.Ordinal)))
            {
                throw new StateConflictException(key, expectedETag is null ? "it already exists" : null);
            }

            throw Fail("write", key, result);
        }

        return JsonNode.Parse(result.Stdout)!["metadata"]!["resourceVersion"]!.GetValue<string>();
    }

    public async Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default)
    {
        if (expectedETag is not null)
        {
            var current = await ReadAsync(key, ct);
            if (current is null)
            {
                return;
            }

            if (current.ETag != expectedETag)
            {
                throw new StateConflictException(key);
            }
        }

        var result = await RunAsync(["delete", "secret", SecretName(key), "--ignore-not-found"], ct);
        if (!result.Ok)
        {
            throw Fail("delete", key, result);
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        // Only metadata is needed; a jsonpath keeps secret values out of the output.
        var result = await RunAsync(["get", "secrets", "-l", $"{StateLabel}=true", "-o",
            "jsonpath={range .items[*]}{.metadata.annotations.deployify\\.io/key}{\"\\n\"}{end}"], ct);
        if (!result.Ok)
        {
            throw Fail("list", prefix, result);
        }

        return
        [
            .. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }

    internal IReadOnlyList<string> BuildArgs(IReadOnlyList<string> command)
    {
        var args = new List<string>();
        if (config.KubeConfigPath is not null)
        {
            args.AddRange(["--kubeconfig", config.KubeConfigPath]);
        }

        if (config.Context is not null)
        {
            args.AddRange(["--context", config.Context]);
        }

        args.AddRange(["-n", config.Namespace]);
        args.AddRange(command);
        return args;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct, bool sensitive = false, string? stdin = null)
    {
        try
        {
            return await _processes.RunAsync(new ProcessSpec("kubectl", BuildArgs(command)) { Sensitive = sensitive, Stdin = stdin }, ct);
        }
        catch (ToolNotFoundException ex)
        {
            throw new StateBackendException("The Kubernetes state backend needs kubectl on PATH.", ex);
        }
    }

    private StateBackendException Fail(string action, string key, ProcessResult r)
        => new($"Kubernetes state {action} of '{key}' in namespace {config.Namespace} failed: {r.ErrorOrOutput.Trim()}");
}
