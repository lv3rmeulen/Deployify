using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

public sealed class NomadClient : IDisposable
{
    /// <summary>Job meta key Deployify sets on every job it generates.</summary>
    public const string ManagedMetaKey = "deployify-managed";

    private readonly HttpClient _http;
    private readonly ILogger<NomadClient> _logger;
    private readonly string? _registryPassword;

    public NomadClient(string address, string? token, ILogger<NomadClient> logger,
        string @namespace = "default", string? registryPassword = null)
    {
        _logger = logger;
        _registryPassword = registryPassword;
        Ns = string.IsNullOrWhiteSpace(@namespace) ? "default" : @namespace;
        _http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Add("X-Nomad-Token", token);
        }
    }

    private string Ns => $"namespace={Uri.EscapeDataString(field)}";

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            return (await _http.GetAsync("v1/agent/self", ct)).IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nomad ping failed.");
            return false;
        }
    }

    /// <summary>
    /// Names of jobs in the configured namespace that are not dead. When
    /// <paramref name="managedOnly"/> is true only jobs Deployify created are returned,
    /// so plan never proposes deleting another team's jobs.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRunningJobNamesAsync(
        bool managedOnly = false, CancellationToken ct = default)
    {
        var jobs = await _http.GetFromJsonAsync<List<NomadJobStub>>($"v1/jobs?{Ns}&meta=true", ct) ?? [];
        return
        [
            .. jobs
                .Where(j => !string.Equals(j.Status, "dead", StringComparison.OrdinalIgnoreCase))
                .Where(j => !managedOnly || (j.Meta is not null && j.Meta.ContainsKey(ManagedMetaKey)))
                .Select(j => j.Id)
        ];
    }

    /// <summary>
    /// Asks Nomad what submitting <paramref name="hclPath"/> would change, using the job
    /// plan API. Returns Nomad's diff type: "None", "Added", "Edited" or "Deleted".
    /// </summary>
    public async Task<string> PlanJobAsync(string hclPath, CancellationToken ct = default)
    {
        var (jobId, jobJson) = await ParseAsync(hclPath, ct);
        var resp = await _http.PostAsync($"v1/job/{Uri.EscapeDataString(jobId)}/plan?{Ns}",
            new StringContent($"{{\"Job\":{jobJson},\"Diff\":true}}", Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(resp, "plan", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("Diff", out var diff) &&
               diff.ValueKind == JsonValueKind.Object &&
               diff.TryGetProperty("Type", out var type)
            ? type.GetString() ?? "None"
            : "None";
    }

    public async Task<string> SubmitJobAsync(string hclPath, CancellationToken ct = default)
    {
        var (_, jobJson) = await ParseAsync(hclPath, ct);
        var submitResp = await _http.PostAsync($"v1/jobs?{Ns}",
            new StringContent($"{{\"Job\":{jobJson}}}", Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(submitResp, "submit", ct);
        using var doc = JsonDocument.Parse(await submitResp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("EvalID", out var id) ? id.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// Creates or replaces the Nomad Variable at <paramref name="path"/> with <paramref name="items"/>
    /// (PUT /v1/var/:path) in the configured namespace.
    /// </summary>
    public async Task PutVariableAsync(string path, IReadOnlyDictionary<string, string> items, CancellationToken ct = default)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var resp = await _http.PutAsJsonAsync($"v1/var/{escaped}?{Ns}", new { Path = path, Items = items }, ct);
        await EnsureSuccessAsync(resp, $"write variable {path}", ct);
    }

    public async Task StopJobAsync(string jobName, bool purge = false, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(
            $"v1/job/{Uri.EscapeDataString(jobName)}?{Ns}&purge={purge.ToString().ToLowerInvariant()}", ct);
        await EnsureSuccessAsync(resp, "stop", ct);
    }

    public async Task<IReadOnlyList<string>> ValidateJobAsync(string hclPath, CancellationToken ct = default)
    {
        var hcl = await ReadHclAsync(hclPath, ct);
        var parseResp = await _http.PostAsJsonAsync($"v1/jobs/parse?{Ns}", new { JobHCL = hcl, Canonicalize = true }, ct);
        if (!parseResp.IsSuccessStatusCode)
        {
            return [$"Parse error: {await parseResp.Content.ReadAsStringAsync(ct)}"];
        }

        var jobJson = await parseResp.Content.ReadAsStringAsync(ct);
        var valResp = await _http.PostAsync("v1/validate/job",
            new StringContent($"{{\"Job\":{jobJson}}}", Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(valResp, "validate", ct);
        using var doc = JsonDocument.Parse(await valResp.Content.ReadAsStringAsync(ct));
        var errors = new List<string>();
        if (doc.RootElement.TryGetProperty("ValidationErrors", out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in el.EnumerateArray())
            {
                errors.Add(e.GetString() ?? "");
            }
        }

        return errors;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Reads a job file and swaps the registry password placeholder for the real secret.</summary>
    private async Task<string> ReadHclAsync(string hclPath, CancellationToken ct)
        => Hcl.InjectRegistryPassword(await File.ReadAllTextAsync(hclPath, ct), _registryPassword);

    private async Task<(string JobId, string JobJson)> ParseAsync(string hclPath, CancellationToken ct)
    {
        var hcl = await ReadHclAsync(hclPath, ct);
        var parseResp = await _http.PostAsJsonAsync($"v1/jobs/parse?{Ns}", new { JobHCL = hcl, Canonicalize = true }, ct);
        await EnsureSuccessAsync(parseResp, $"parse {Path.GetFileName(hclPath)}", ct);
        var jobJson = await parseResp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(jobJson);
        var id = doc.RootElement.TryGetProperty("ID", out var idEl) ? idEl.GetString() : null;
        return (id ?? Path.GetFileNameWithoutExtension(hclPath).Replace(".job", ""), jobJson);
    }

    /// <summary>Throws with Nomad's own error text instead of a bare status code.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Nomad {action} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {body.Trim()}");
    }

    private sealed class NomadJobStub
    {
        [JsonPropertyName("ID")] public string Id { get; } = string.Empty;
        [JsonPropertyName("Status")] public string? Status { get; }
        [JsonPropertyName("Meta")] public Dictionary<string, string>? Meta { get; }
    }
}
