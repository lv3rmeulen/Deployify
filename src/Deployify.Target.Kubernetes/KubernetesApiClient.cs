using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Kubernetes;

/// <summary>
/// Thin wrapper over the Kubernetes HTTP API.
/// Used as fallback when kubectl is unavailable.
/// Reads credentials from the kubeconfig or in-cluster service account.
/// </summary>
public sealed class KubernetesApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public KubernetesApiClient(KubernetesTargetConfig config, ILogger logger)
    {
        _logger = logger;

        var (server, token, caCert) = ResolveCredentials(config);

        var handler = new HttpClientHandler();

        if (caCert is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert?.Issuer.Contains("kubernetes", StringComparison.OrdinalIgnoreCase) == true;
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
        _logger.LogError("K8s API error {Status}: {Body}", resp.StatusCode, body);
        return (false, $"HTTP {(int)resp.StatusCode}: {body}");
    }

    public async Task<IReadOnlyList<string>> GetNamespacesWithPrefixAsync(
        string prefix, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>("api/v1/namespaces", ct);
            var items = resp.GetProperty("items");
            return items.EnumerateArray()
                .Select(i => i.GetProperty("metadata").GetProperty("name").GetString() ?? "")
                .Where(n => n.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();
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
            return items.EnumerateArray()
                .Select(i => i.GetProperty("metadata").GetProperty("name").GetString() ?? "")
                .ToList();
        }
        catch { return []; }
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
        _ => kind.ToLowerInvariant() + "s"
    };

    private static (string Server, string? Token, string? CaCert) ResolveCredentials(
        KubernetesTargetConfig config)
    {
        // Try kubeconfig file
        var kubeconfigPath = config.KubeConfigPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kube", "config");

        if (File.Exists(kubeconfigPath))
        {
            var (server, token) = ParseKubeconfig(kubeconfigPath, config.Context);
            if (server is not null)
            {
	            return (server, token, null);
            }
        }

        // Try in-cluster service account
        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        const string caCertPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

        if (File.Exists(tokenPath))
        {
            var token = File.ReadAllText(tokenPath).Trim();
            var caCert = File.Exists(caCertPath) ? File.ReadAllText(caCertPath) : null;
            return ("https://kubernetes.default.svc", token, caCert);
        }

        return ("https://localhost:6443", null, null);
    }

    private static (string? Server, string? Token) ParseKubeconfig(string path, string? contextName)
    {
        try
        {
            // Minimal kubeconfig YAML parsing — reads server + token for the active/named context
            var yaml = File.ReadAllText(path);
            var lines = yaml.Split('\n');

            // Find current-context if not specified
            if (contextName is null)
            {
                var ccLine = lines.FirstOrDefault(l => l.StartsWith("current-context:"));
                contextName = ccLine?.Split(':', 2).LastOrDefault()?.Trim().Trim('"');
            }

            // Extract server - very naive but covers standard kubeconfig format
            var serverLine = lines.SkipWhile(l => !l.Contains("server:")).FirstOrDefault();
            var server = serverLine?.Split("server:", 2).LastOrDefault()?.Trim();

            // Extract token if present
            var tokenLine = lines.SkipWhile(l => !l.Contains("token:")).FirstOrDefault();
            var token = tokenLine?.Split("token:", 2).LastOrDefault()?.Trim();

            return (server, token);
        }
        catch { return (null, null); }
    }
}
