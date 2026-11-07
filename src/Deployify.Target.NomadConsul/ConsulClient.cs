using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Lightweight HTTP client for the Consul agent API.
/// Used only to verify connectivity (ping) and to query registered services
/// for the plan diff. Job submission still goes through Nomad.
/// </summary>
public sealed class ConsulClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public ConsulClient(string address, string? token, ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Add("X-Consul-Token", token);
        }
    }

    /// <summary>
    /// Returns true when the Consul agent responds to /v1/agent/self.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("v1/agent/self", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Consul ping failed.");
            return false;
        }
    }

    /// <summary>
    /// Returns the names of services currently registered in the Consul catalog.
    /// Uses /v1/catalog/services which returns a dict of service name → tags.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRegisteredServiceNamesAsync(
        string? consulNamespace = null, CancellationToken ct = default)
    {
        try
        {
            var url = "v1/catalog/services";
            if (!string.IsNullOrWhiteSpace(consulNamespace))
            {
                url += $"?ns={Uri.EscapeDataString(consulNamespace)}";
            }

            var resp = await _http.GetFromJsonAsync<Dictionary<string, string[]>>(url, ct);
            // Filter out the built-in "consul" service
            return resp?.Keys.Where(k => k != "consul").ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list Consul services.");
            return [];
        }
    }

    /// <summary>
    /// Checks whether a specific service has at least one healthy instance.
    /// </summary>
    public async Task<bool> IsServiceHealthyAsync(
        string serviceName, CancellationToken ct = default)
    {
        try
        {
            var url = $"v1/health/service/{Uri.EscapeDataString(serviceName)}?passing=true";
            var instances = await _http.GetFromJsonAsync<List<ConsulHealthEntry>>(url, ct);
            return instances is { Count: > 0 };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Consul health check for '{Service}' failed.", serviceName);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class ConsulHealthEntry
    {
        [JsonPropertyName("Service")]
        public ConsulServiceEntry? Service { get; set; }
    }

    private sealed class ConsulServiceEntry
    {
        [JsonPropertyName("ID")]
        public string Id { get; set; } = string.Empty;
    }
}
