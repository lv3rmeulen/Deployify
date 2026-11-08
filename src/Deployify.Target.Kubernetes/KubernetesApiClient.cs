using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.SecretBackends;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Thin wrapper over the Kubernetes HTTP API.
/// Used as fallback when kubectl is unavailable.
/// Reads credentials from the kubeconfig or in-cluster service account.
/// </summary>
public sealed class KubernetesApiClient : IDisposable, IKubernetesSecretClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Creates a client, reading the service account credentials asynchronously.</summary>
    public static async Task<KubernetesApiClient> CreateAsync(
        KubernetesTargetConfig config, ILogger logger, CancellationToken ct = default)
    {
        var (server, token, caCert) = await ResolveCredentialsAsync(config, ct);
        return new KubernetesApiClient(server, token, caCert, logger);
    }

    private KubernetesApiClient(string server, string? token, string? caCert, ILogger logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler();
        if (caCert is not null)
        {
            // Trust exactly the cluster CA: build the chain against it instead of the
            // system store, and require the chain to end in that certificate.
            var ca = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(caCert);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null)
                {
                    return false;
                }

                if (errors == System.Net.Security.SslPolicyErrors.None)
                {
                    return true;
                }

                if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                {
                    return false;
                }

                using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(server.TrimEnd('/') + "/") };

        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("api/v1", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kubernetes API ping failed.");
            return false;
        }
    }

    /// <summary>Applies a parsed YAML document as a JSON patch to the API.</summary>
    public async Task<(bool Ok, string? Error)> ApplyAsync(
        string resourceKind,
        string apiVersion,
        string namespaceName,
        string resourceName,
        string jsonBody,
        CancellationToken ct = default)
    {
        // Use server-side apply (PATCH with application/apply-patch+yaml)
        var group = apiVersion.Contains('/') ? apiVersion.Split('/')[0] : string.Empty;
        var version = apiVersion.Contains('/') ? apiVersion.Split('/')[1] : apiVersion;

        var url = BuildResourceUrl(group, version, resourceKind, namespaceName, resourceName);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/apply-patch+yaml");
        var resp = await _http.PatchAsync(
            $"{url}?fieldManager=deployify&force=true", content, ct);

        if (resp.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogError("Kubernetes API error {Status}: {Body}", resp.StatusCode, body);
        return (false, $"HTTP {(int)resp.StatusCode}: {body}");
    }

    public async Task<IReadOnlyList<string>> GetNamespacesWithPrefixAsync(
        string prefix, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(
                "api/v1/namespaces?labelSelector=" + Uri.EscapeDataString(KubectlRunner.ManagedBySelector), ct);
            var items = resp.GetProperty("items");
            return
            [
                .. items.EnumerateArray()
                    .Select(i => i.GetProperty("metadata").GetProperty("name").GetString() ?? "")
                    .Where(n => n.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            ];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list namespaces.");
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetDeploymentNamesInNamespaceAsync(
        string namespaceName, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(
                $"apis/apps/v1/namespaces/{namespaceName}/deployments", ct);
            var items = resp.GetProperty("items");
            return
            [
                .. items.EnumerateArray()
                    .Select(i => i.GetProperty("metadata").GetProperty("name").GetString() ?? "")
            ];
        }
        catch
        {
            return [];
        }
    }

    // ── Secrets ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, SensitiveValue>?> GetSecretDataAsync(
        string namespaceName, string name, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildResourceUrl("", "v1", "Secret", namespaceName, name), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            // The body of a failed request doesn't hold secret data, but don't log it anyway.
            throw new InvalidOperationException($"Could not read secret {namespaceName}/{name}: HTTP {(int)resp.StatusCode}");
        }

        return KubernetesSecretManifest.ReadData(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <inheritdoc />
    public async Task<string?> GetResourceVersionAsync(string kind, string namespaceName, string name, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(BuildResourceUrl("", "v1", kind, namespaceName, name), ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString();
    }

    /// <inheritdoc />
    public async Task<string> ServerSideApplyAsync(string json, CancellationToken ct = default)
    {
        using var obj = JsonDocument.Parse(json);
        var root = obj.RootElement;
        var meta = root.GetProperty("metadata");
        var apiVersion = root.GetProperty("apiVersion").GetString()!;
        var group = apiVersion.Contains('/') ? apiVersion.Split('/')[0] : string.Empty;
        var version = apiVersion.Contains('/') ? apiVersion.Split('/')[1] : apiVersion;
        var ns = meta.TryGetProperty("namespace", out var n) ? n.GetString() ?? "" : "";
        var url = BuildResourceUrl(group, version, root.GetProperty("kind").GetString()!, ns, meta.GetProperty("name").GetString()!);

        using var content = new StringContent(json, Encoding.UTF8, "application/apply-patch+yaml");
        using var resp = await _http.PatchAsync($"{url}?fieldManager=deployify&force=true", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Error bodies can echo the submitted object, which may be a Secret: report the status only.
            throw new InvalidOperationException($"Kubernetes API apply failed: HTTP {(int)resp.StatusCode}");
        }

        using var result = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return result.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString() ?? "";
    }

    public void Dispose() => _http.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildResourceUrl(
        string group, string version, string kind, string namespaceName, string name)
    {
        var kindPlural = PluralizeKind(kind);
        var apiBase = string.IsNullOrEmpty(group) ? $"api/{version}" : $"apis/{group}/{version}";
        return string.IsNullOrEmpty(namespaceName)
            ? $"{apiBase}/{kindPlural}/{name}"
            : $"{apiBase}/namespaces/{namespaceName}/{kindPlural}/{name}";
    }

    private static string PluralizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "namespace" => "namespaces",
        "deployment" => "deployments",
        "service" => "services",
        "configmap" => "configmaps",
        "ingress" => "ingresses",
        "persistentvolumeclaim" => "persistentvolumeclaims",
        "secretproviderclass" => "secretproviderclasses",
        _ => kind.ToLowerInvariant() + "s"
    };

    /// <summary>
    /// The API fallback only runs inside a cluster, with the pod's service account.
    /// Outside a cluster kubectl is required: a kubeconfig can hold many contexts, client
    /// certificates and exec credential plugins, and guessing at those risks deploying to
    /// the wrong cluster.
    /// </summary>
    private static async Task<(string Server, string? Token, string? CaCert)> ResolveCredentialsAsync(
        KubernetesTargetConfig config, CancellationToken ct)
    {
        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        const string caCertPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

        if (File.Exists(tokenPath) && File.Exists(caCertPath))
        {
            var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "kubernetes.default.svc";
            var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
            var token = await File.ReadAllTextAsync(tokenPath, ct);
            var caCert = await File.ReadAllTextAsync(caCertPath, ct);
            return ($"https://{host}:{port}", token.Trim(), caCert);
        }

        throw new InvalidOperationException(
            "kubectl was not found on PATH. Install kubectl (it reads your kubeconfig" +
            (config.Context is null ? "" : $" and context '{config.Context}'") +
            "), or run deployify inside the cluster with a service account.");
    }
}
