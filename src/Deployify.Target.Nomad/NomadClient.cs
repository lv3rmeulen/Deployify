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
    /// Every job in the namespace that carries Deployify's meta key, stopped (dead) ones included,
    /// so destroy also purges jobs a rollback took out of service. Throws when Nomad can't be reached.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetManagedJobNamesAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/jobs?{Ns}&meta=true", ct);
        await EnsureSuccessAsync(resp, "list jobs", ct);
        var jobs = await resp.Content.ReadFromJsonAsync<List<NomadJobStub>>(ct) ?? [];
        return [.. jobs.Where(j => j.Meta is not null && j.Meta.ContainsKey(ManagedMetaKey)).Select(j => j.Id)];
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
        var resp = await _http.PutAsJsonAsync($"v1/var/{EscapePath(path)}?{Ns}", new { Path = path, Items = items }, ct);
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

    // ── Rollout ───────────────────────────────────────────────────────────────

    /// <summary>The job's current version and status, or null when Nomad doesn't know the job.</summary>
    public async Task<NomadJobInfo?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/job/{Uri.EscapeDataString(jobId)}?{Ns}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(resp, $"read job {jobId}", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new NomadJobInfo(
            root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0,
            root.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
            root.TryGetProperty("Stop", out var stop) && stop.ValueKind == JsonValueKind.True);
    }

    /// <summary>All deployments of the job (GET /v1/job/:id/deployments), newest job version first.</summary>
    public async Task<IReadOnlyList<NomadDeploymentInfo>> GetDeploymentsAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/job/{Uri.EscapeDataString(jobId)}/deployments?{Ns}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureSuccessAsync(resp, $"read deployments of {jobId}", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<NomadDeploymentInfo>();
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            int desired = 0, healthy = 0, unhealthy = 0;
            if (d.TryGetProperty("TaskGroups", out var groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (var g in groups.EnumerateObject())
                {
                    desired += Int(g.Value, "DesiredTotal");
                    healthy += Int(g.Value, "HealthyAllocs");
                    unhealthy += Int(g.Value, "UnhealthyAllocs");
                }
            }

            result.Add(new NomadDeploymentInfo(
                d.TryGetProperty("JobVersion", out var jv) && jv.ValueKind == JsonValueKind.Number ? jv.GetInt64() : -1,
                d.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
                d.TryGetProperty("StatusDescription", out var sd) ? sd.GetString() : null,
                desired, healthy, unhealthy,
                d.TryGetProperty("CreateIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt64() : 0));
        }

        return [.. result.OrderByDescending(r => r.JobVersion).ThenByDescending(r => r.CreateIndex)];

        static int Int(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    /// <summary>
    /// Reverts the job to <paramref name="version"/> (POST /v1/job/:id/revert). Nomad registers the
    /// old spec as a new job version, which then rolls out like any other.
    /// </summary>
    public async Task RevertJobAsync(string jobId, long version, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"v1/job/{Uri.EscapeDataString(jobId)}/revert?{Ns}",
            new { JobID = jobId, JobVersion = version }, ct);
        await EnsureSuccessAsync(resp, $"revert {jobId} to version {version}", ct);
    }

    /// <summary>
    /// The items of the Nomad Variable at <paramref name="path"/>, or null when it doesn't exist.
    /// The values are secrets: keep them in memory only.
    /// </summary>
    public async Task<Dictionary<string, string>?> GetVariableItemsAsync(string path, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/var/{EscapePath(path)}?{Ns}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(resp, $"read variable {path}", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var items = new Dictionary<string, string>();
        if (doc.RootElement.TryGetProperty("Items", out var el) && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                items[p.Name] = p.Value.GetString() ?? "";
            }
        }

        return items;
    }

    /// <summary>Paths of the variables under <paramref name="prefix"/> (GET /v1/vars?prefix=…). No values.</summary>
    public async Task<IReadOnlyList<string>> ListVariablePathsAsync(string prefix, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/vars?prefix={Uri.EscapeDataString(prefix)}&{Ns}", ct);
        await EnsureSuccessAsync(resp, $"list variables under {prefix}", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var paths = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                if (v.TryGetProperty("Path", out var p) && p.GetString() is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    public async Task DeleteVariableAsync(string path, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"v1/var/{EscapePath(path)}?{Ns}", ct);
        await EnsureSuccessAsync(resp, $"delete variable {path}", ct);
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

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
        [JsonPropertyName("ID")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("Status")] public string? Status { get; init; }
        [JsonPropertyName("Meta")] public Dictionary<string, string>? Meta { get; init; }
    }
}

/// <summary>The parts of a Nomad job the rollout needs.</summary>
public sealed record NomadJobInfo(long Version, string Status, bool Stop);

/// <summary>One deployment of a job, with its task groups' allocation counts added up.</summary>
public sealed record NomadDeploymentInfo(
    long JobVersion,
    string Status,
    string? StatusDescription,
    int DesiredTotal,
    int HealthyAllocs,
    int UnhealthyAllocs,
    long CreateIndex);
