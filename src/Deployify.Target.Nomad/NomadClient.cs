using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Target.Nomad.Api;
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
    /// Asks Nomad what submitting the job file at <paramref name="jobPath"/> would change, using
    /// the job plan API. Returns Nomad's diff type: "None", "Added", "Edited" or "Deleted".
    /// </summary>
    public async Task<string> PlanJobAsync(string jobPath, CancellationToken ct = default)
    {
        var (jobId, job) = await ReadJobAsync(jobPath, ct);
        var resp = await _http.PostAsync($"v1/job/{Uri.EscapeDataString(jobId)}/plan?{Ns}",
            JsonBody(new JsonObject { ["Job"] = job, ["Diff"] = true }), ct);
        await EnsureSuccessAsync(resp, "plan", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("Diff", out var diff) &&
               diff.ValueKind == JsonValueKind.Object &&
               diff.TryGetProperty("Type", out var type)
            ? type.GetString() ?? "None"
            : "None";
    }

    public async Task<string> SubmitJobAsync(string jobPath, CancellationToken ct = default)
    {
        var (_, job) = await ReadJobAsync(jobPath, ct);
        var submitResp = await _http.PostAsync($"v1/jobs?{Ns}", JsonBody(new JsonObject { ["Job"] = job }), ct);
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

    public async Task<IReadOnlyList<string>> ValidateJobAsync(string jobPath, CancellationToken ct = default)
    {
        var (_, job) = await ReadJobAsync(jobPath, ct);
        var valResp = await _http.PostAsync("v1/validate/job", JsonBody(new JsonObject { ["Job"] = job }), ct);
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
            int desired = 0, healthy = 0, unhealthy = 0, canaries = 0;
            var promoted = true;
            if (d.TryGetProperty("TaskGroups", out var groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (var g in groups.EnumerateObject())
                {
                    desired += Int(g.Value, "DesiredTotal");
                    healthy += Int(g.Value, "HealthyAllocs");
                    unhealthy += Int(g.Value, "UnhealthyAllocs");
                    var groupCanaries = Int(g.Value, "DesiredCanaries");
                    canaries += groupCanaries;
                    if (groupCanaries > 0 && !(g.Value.TryGetProperty("Promoted", out var p) && p.ValueKind == JsonValueKind.True))
                    {
                        promoted = false;
                    }
                }
            }

            result.Add(new NomadDeploymentInfo(
                d.TryGetProperty("JobVersion", out var jv) && jv.ValueKind == JsonValueKind.Number ? jv.GetInt64() : -1,
                d.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
                d.TryGetProperty("StatusDescription", out var sd) ? sd.GetString() : null,
                desired, healthy, unhealthy,
                d.TryGetProperty("CreateIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt64() : 0)
            {
                Id = d.TryGetProperty("ID", out var id) ? id.GetString() : null,
                DesiredCanaries = canaries,
                Promoted = canaries == 0 || promoted
            });
        }

        return [.. result.OrderByDescending(r => r.JobVersion).ThenByDescending(r => r.CreateIndex)];

        static int Int(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    /// <summary>Promotes every canary of a deployment (POST /v1/deployment/promote/:id).</summary>
    public async Task PromoteDeploymentAsync(string deploymentId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"v1/deployment/promote/{Uri.EscapeDataString(deploymentId)}?{Ns}",
            new { DeploymentID = deploymentId, All = true }, ct);
        await EnsureSuccessAsync(resp, $"promote deployment {deploymentId}", ct);
    }

    /// <summary>
    /// Marks a deployment failed (POST /v1/deployment/fail/:id), which stops its canaries and, with
    /// auto_revert, makes Nomad go back to the last stable version.
    /// </summary>
    public async Task FailDeploymentAsync(string deploymentId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"v1/deployment/fail/{Uri.EscapeDataString(deploymentId)}?{Ns}",
            new { DeploymentID = deploymentId }, ct);
        await EnsureSuccessAsync(resp, $"fail deployment {deploymentId}", ct);
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

    // ── Resource server ───────────────────────────────────────────────────────

    /// <summary>Raw JSON of every job in the namespace with its meta (GET /v1/jobs?meta=true). Throws when Nomad can't be reached.</summary>
    public async Task<string> GetJobListJsonAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/jobs?{Ns}&meta=true", ct);
        await EnsureSuccessAsync(resp, "list jobs", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Raw JSON of one job (GET /v1/job/:id), or null when Nomad doesn't know it.</summary>
    public async Task<string?> GetJobJsonAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/job/{Uri.EscapeDataString(jobId)}?{Ns}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(resp, $"read job {jobId}", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Raw JSON of every allocation in the namespace, with task states and allocated ports
    /// (GET /v1/allocations?resources=true): one request however many jobs there are.
    /// </summary>
    public async Task<string> GetAllocationsJsonAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/allocations?{Ns}&resources=true&task_states=true", ct);
        await EnsureSuccessAsync(resp, "list allocations", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Lines of a task's stdout or stderr (GET /v1/client/fs/logs/:alloc_id, plain text). Starts
    /// about <paramref name="tailBytes"/> before the end (the first, partial line is dropped), or
    /// at the start of the oldest log file when it is 0;
    /// with <paramref name="follow"/> keeps streaming until cancelled.
    /// </summary>
    public async IAsyncEnumerable<string> StreamTaskLogsAsync(
        string allocId, string task, bool stderr, bool follow, long tailBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"v1/client/fs/logs/{Uri.EscapeDataString(allocId)}?{Ns}&task={Uri.EscapeDataString(task)}" +
                  $"&type={(stderr ? "stderr" : "stdout")}&follow={(follow ? "true" : "false")}" +
                  (tailBytes > 0
                      ? $"&origin=end&offset={tailBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                      : "&origin=start&offset=0") + "&plain=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Headers only: the body is a stream that stays open while following.
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(resp, $"read logs of allocation {allocId}", ct);
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var first = tailBytes > 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (first)
            {
                // Starting mid-file almost always means starting mid-line.
                first = false;
                continue;
            }

            yield return line;
        }
    }

    /// <summary>The job's allocations, any status (GET /v1/job/:id/allocations), as JSON.</summary>
    public async Task<string> GetJobAllocationsJsonAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/job/{Uri.EscapeDataString(jobId)}/allocations?{Ns}", ct);
        await EnsureSuccessAsync(resp, $"list allocations of job {jobId}", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Lines of a task's stdout or stderr (GET /v1/client/fs/logs/:alloc_id, plain text), starting
    /// <paramref name="bytesFromEnd"/> bytes before the end (the first, partial line dropped), at
    /// the end when 0, or at the start of the oldest log file Nomad keeps when null; with
    /// <paramref name="follow"/> keeps streaming until cancelled.
    /// </summary>
    public async IAsyncEnumerable<string> StreamTaskLogLinesAsync(
        string allocId, string task, bool stderr, bool follow, long? bytesFromEnd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var origin = bytesFromEnd is { } n
            ? $"&origin=end&offset={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "&origin=start&offset=0";
        var url = $"v1/client/fs/logs/{Uri.EscapeDataString(allocId)}?{Ns}&task={Uri.EscapeDataString(task)}" +
                  $"&type={(stderr ? "stderr" : "stdout")}&follow={(follow ? "true" : "false")}{origin}&plain=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The task never wrote to this stream, or its allocation's files are gone.
            yield break;
        }

        await EnsureSuccessAsync(resp, $"read logs of allocation {allocId}", ct);
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var skipPartial = bytesFromEnd > 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (skipPartial)
            {
                skipPartial = false;
                continue;
            }

            yield return line;
        }
    }

    /// <summary>Restarts every task of an allocation in place (POST /v1/client/allocation/:id/restart).</summary>
    public async Task RestartAllocationAsync(string allocId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"v1/client/allocation/{Uri.EscapeDataString(allocId)}/restart?{Ns}",
            new { AllTasks = true }, ct);
        await EnsureSuccessAsync(resp, $"restart allocation {allocId}", ct);
    }

    /// <summary>Stops an allocation so the scheduler places a replacement (POST /v1/allocation/:id/stop).</summary>
    public async Task StopAllocationAsync(string allocId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"v1/allocation/{Uri.EscapeDataString(allocId)}/stop?{Ns}", null, ct);
        await EnsureSuccessAsync(resp, $"stop allocation {allocId}", ct);
    }

    /// <summary>
    /// Starts a stopped job again by registering its current spec with <c>Stop = false</c>, which is
    /// what <c>nomad job start</c> does.
    /// </summary>
    public async Task StartJobAsync(string jobId, CancellationToken ct = default)
    {
        var json = await GetJobJsonAsync(jobId, ct)
                   ?? throw new HttpRequestException($"Nomad doesn't know job {jobId}.");
        var job = JsonNode.Parse(json)!.AsObject();
        job["Stop"] = false;
        var resp = await _http.PostAsync($"v1/jobs?{Ns}",
            new StringContent(new JsonObject { ["Job"] = job }.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(resp, $"start job {jobId}", ct);
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();

    /// <summary>Reads a job file, with the registry password placeholder swapped for the real secret.</summary>
    private Task<(string JobId, JsonObject Job)> ReadJobAsync(string jobPath, CancellationToken ct)
        => NomadJobFile.ReadAsync(jobPath, _registryPassword, ct);

    private static StringContent JsonBody(JsonObject body)
        => new(body.ToJsonString(), Encoding.UTF8, "application/json");

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
    long CreateIndex)
{
    public string? Id { get; init; }

    /// <summary>Canaries the deployment places next to the old allocations (0 for a rolling update).</summary>
    public int DesiredCanaries { get; init; }

    /// <summary>False while the deployment's canaries wait for promotion.</summary>
    public bool Promoted { get; init; } = true;

    /// <summary>Canaries placed and waiting for <c>promote</c>.</summary>
    public bool AwaitingPromotion => Status == "running" && DesiredCanaries > 0 && !Promoted;
}
