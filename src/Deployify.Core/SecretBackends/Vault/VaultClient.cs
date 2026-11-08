using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.SecretBackends.Vault;

/// <summary>A KV v2 entry: its values and its version (0 when it doesn't exist).</summary>
public sealed record VaultKvEntry(IReadOnlyDictionary<string, SensitiveValue> Data, int Version)
{
    public static readonly VaultKvEntry Missing = new(new Dictionary<string, SensitiveValue>(), 0);
}

/// <summary>
/// Minimal Vault HTTP API client: KV v2 read/write, ACL policies and Kubernetes auth roles.
/// Uses the HTTP API directly so Deployify needs no Vault CLI. Response bodies are never
/// logged or put in exception messages, since a KV read returns secret values.
/// </summary>
public sealed class VaultClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public VaultClient(Uri address, string token, string? vaultNamespace = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrEmpty(token);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = new Uri(address.ToString().TrimEnd('/') + "/v1/");
        _http.DefaultRequestHeaders.Add("X-Vault-Token", token);
        _http.DefaultRequestHeaders.Add("X-Vault-Request", "true");
        if (!string.IsNullOrEmpty(vaultNamespace))
        {
            _http.DefaultRequestHeaders.Add("X-Vault-Namespace", vaultNamespace);
        }
    }

    /// <summary>
    /// Creates a client from <see cref="VaultConfig"/>, falling back to the <c>VAULT_ADDR</c>,
    /// <c>VAULT_TOKEN</c> and <c>VAULT_NAMESPACE</c> environment variables.
    /// </summary>
    public static VaultClient FromConfig(VaultConfig config, HttpMessageHandler? handler = null)
    {
        var address = Models.Secrets.Resolve(config.Address, "VAULT_ADDR")
            ?? throw new InvalidOperationException(
                "Vault address not set. Set secrets.vault.address or the VAULT_ADDR environment variable.");
        var token = Models.Secrets.Resolve(config.Token, "VAULT_TOKEN")
            ?? throw new InvalidOperationException(
                "Vault token not set. Set secrets.vault.token to \"env:NAME\" or set VAULT_TOKEN.");
        return new VaultClient(new Uri(address), token, Models.Secrets.Resolve(config.Namespace, "VAULT_NAMESPACE"), handler);
    }

    /// <summary>Reads a KV v2 entry. Returns <see cref="VaultKvEntry.Missing"/> when it doesn't exist (or was deleted).</summary>
    public async Task<VaultKvEntry> ReadKvAsync(string mount, string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(KvUrl(mount, "data", path), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return VaultKvEntry.Missing;
        }

        await EnsureSuccessAsync(resp, $"read {mount}/{path}", ct);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = doc.RootElement.GetProperty("data");
        var version = data.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("version", out var v)
            ? v.GetInt32() : 0;
        var values = new Dictionary<string, SensitiveValue>(StringComparer.Ordinal);
        if (data.TryGetProperty("data", out var kv) && kv.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in kv.EnumerateObject())
            {
                values[prop.Name] = new SensitiveValue(prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()!
                    : prop.Value.GetRawText());
            }
        }

        return new VaultKvEntry(values, version);
    }

    /// <summary>
    /// Writes a whole KV v2 entry. <paramref name="cas"/> is the version the caller read (0 for
    /// "must not exist yet"), so a concurrent write fails instead of being overwritten.
    /// Returns the new version.
    /// </summary>
    public async Task<int> WriteKvAsync(
        string mount, string path, IReadOnlyDictionary<string, SensitiveValue> data, int? cas = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["data"] = new JsonObject(data.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value.Reveal()))))
        };
        if (cas is not null)
        {
            body["options"] = new JsonObject { ["cas"] = cas.Value };
        }

        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(KvUrl(mount, "data", path), content, ct);
        await EnsureSuccessAsync(resp, $"write {mount}/{path}", ct);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
    }

    /// <summary>
    /// Writes <paramref name="data"/> unless the entry already holds exactly those values.
    /// Returns the entry's version and whether it was written.
    /// </summary>
    public async Task<(int Version, bool Written)> WriteKvIfChangedAsync(
        string mount, string path, IReadOnlyDictionary<string, SensitiveValue> data, CancellationToken ct = default)
    {
        var current = await ReadKvAsync(mount, path, ct);
        if (current.Version > 0 && SameData(current.Data, data))
        {
            return (current.Version, false);
        }

        return (await WriteKvAsync(mount, path, data, current.Version, ct), true);
    }

    /// <summary>Creates or replaces an ACL policy.</summary>
    public async Task WritePolicyAsync(string name, string hcl, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync($"sys/policies/acl/{Uri.EscapeDataString(name)}", new { policy = hcl }, ct);
        await EnsureSuccessAsync(resp, $"write policy {name}", ct);
    }

    /// <summary>Creates or replaces a role on the Kubernetes auth method.</summary>
    public async Task WriteKubernetesRoleAsync(
        string authMount, string role, string serviceAccount, string serviceAccountNamespace,
        IReadOnlyList<string> policies, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["bound_service_account_names"] = new[] { serviceAccount },
            ["bound_service_account_namespaces"] = new[] { serviceAccountNamespace },
            ["token_policies"] = policies,
            ["token_ttl"] = "1h"
        };
        using var resp = await _http.PostAsJsonAsync($"auth/{authMount.Trim('/')}/role/{Uri.EscapeDataString(role)}", body, ct);
        await EnsureSuccessAsync(resp, $"write role {role}", ct);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>The full API path of a KV v2 entry, as used in policies and agent templates.</summary>
    public static string KvApiPath(string mount, string path, string kind = "data")
        => $"{mount.Trim('/')}/{kind}/{path.Trim('/')}";

    private static string KvUrl(string mount, string kind, string path)
        => string.Join('/', KvApiPath(mount, path, kind).Split('/').Select(Uri.EscapeDataString));

    private static bool SameData(IReadOnlyDictionary<string, SensitiveValue> a, IReadOnlyDictionary<string, SensitiveValue> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.Equals(kv.Value));

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        // Vault error bodies are {"errors":[...]} and don't echo request data; read only that field.
        var detail = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                detail = ": " + string.Join("; ", errors.EnumerateArray().Select(e => e.ToString()));
            }
        }
        catch (JsonException)
        {
            // No usable detail.
        }

        throw new InvalidOperationException($"Vault could not {action} (HTTP {(int)resp.StatusCode}){detail}");
    }
}
