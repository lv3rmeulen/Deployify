using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.State;

/// <summary>
/// Consul KV. The version tag is the key's ModifyIndex; writes and deletes use <c>?cas=</c>
/// (0 means "only if it doesn't exist"), so Consul itself rejects a conflicting write.
/// </summary>
public sealed class ConsulStateBackend : IStateBackend, IDisposable
{
    private readonly HttpClient _http;

    public ConsulStateBackend(ConsulStateConfig config, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(config.Address.TrimEnd('/') + "/");
        if (Secrets.Resolve(config.Token, "CONSUL_HTTP_TOKEN") is { } token)
        {
            _http.DefaultRequestHeaders.Add("X-Consul-Token", token);
        }

        Description = $"consul {config.Address}";
    }

    public string Description { get; }

    public async Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, Kv(key), null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(resp, $"read {key}", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var entry = doc.RootElement[0];
        var value = entry.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(v.GetString()!)
            : [];
        return new StateBlob(value, entry.GetProperty("ModifyIndex").GetInt64().ToString(CultureInfo.InvariantCulture));
    }

    public async Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"{Kv(key)}?cas={expectedETag ?? "0"}", data, ct);
        await EnsureAsync(resp, $"write {key}", ct);
        if (!await IsTrueAsync(resp, ct))
        {
            throw new StateConflictException(key, expectedETag is null ? "it already exists" : null);
        }

        // Consul doesn't return the new index on a write, so read it back. When the value is no
        // longer ours, someone wrote right after us: hand back a tag that can't match, so our
        // next conditional write fails instead of overwriting theirs.
        var after = await ReadAsync(key, ct);
        return after is not null && after.Data.AsSpan().SequenceEqual(data)
            ? after.ETag
            : expectedETag ?? "0";
    }

    public async Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default)
    {
        var url = expectedETag is null ? Kv(key) : $"{Kv(key)}?cas={expectedETag}";
        using var resp = await SendAsync(HttpMethod.Delete, url, null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureAsync(resp, $"delete {key}", ct);
        if (!await IsTrueAsync(resp, ct))
        {
            // A cas delete of a missing key also returns false.
            if (await ReadAsync(key, ct) is null)
            {
                return;
            }

            throw new StateConflictException(key);
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"{Kv(prefix)}?keys", null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureAsync(resp, $"list {prefix}", ct);
        var keys = JsonSerializer.Deserialize<List<string>>(await resp.Content.ReadAsStringAsync(ct)) ?? [];
        return [.. keys.Where(k => !k.EndsWith('/')).Order(StringComparer.Ordinal)];
    }

    public void Dispose() => _http.Dispose();

    private static string Kv(string key) => "v1/kv/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, byte[]? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
        }

        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StateBackendException($"Cannot reach Consul at {_http.BaseAddress}: {ex.Message}", ex);
        }
    }

    private static async Task<bool> IsTrueAsync(HttpResponseMessage resp, CancellationToken ct)
        => (await resp.Content.ReadAsStringAsync(ct)).Trim() == "true";

    private static async Task EnsureAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new StateBackendException($"Consul state {action} failed ({(int)resp.StatusCode}): {body.Trim()}");
        }
    }
}
