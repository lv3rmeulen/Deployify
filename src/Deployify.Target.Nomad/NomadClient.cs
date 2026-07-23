using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

public sealed class NomadClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<NomadClient> _logger;

    public NomadClient(string address, string? token, ILogger<NomadClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
        {
	        _http.DefaultRequestHeaders.Add("X-Nomad-Token", token);
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { return (await _http.GetAsync("v1/agent/self", ct)).IsSuccessStatusCode; }
        catch (Exception ex) { _logger.LogDebug(ex, "Nomad ping failed."); return false; }
    }

    public async Task<IReadOnlyList<string>> GetRunningJobNamesAsync(CancellationToken ct = default)
    {
        var jobs = await _http.GetFromJsonAsync<List<NomadJobStub>>("v1/jobs", ct) ?? [];
        return jobs.Select(j => j.Id).ToList();
    }

    public async Task<string?> GetJobHclAsync(string jobName, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"v1/job/{Uri.EscapeDataString(jobName)}/hcl?canonicalize=true", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
	        return null;
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> SubmitJobAsync(string hclPath, CancellationToken ct = default)
    {
        var hcl = await File.ReadAllTextAsync(hclPath, ct);
        var parseResp = await _http.PostAsJsonAsync("v1/jobs/parse", new { JobHCL = hcl, Canonicalize = true }, ct);
        parseResp.EnsureSuccessStatusCode();
        var jobJson = await parseResp.Content.ReadAsStringAsync(ct);
        var submitResp = await _http.PostAsync("v1/jobs",
            new StringContent($"{{\"Job\":{jobJson}}}", Encoding.UTF8, "application/json"), ct);
        submitResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await submitResp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("EvalID").GetString() ?? string.Empty;
    }

    public async Task StopJobAsync(string jobName, bool purge = false, CancellationToken ct = default)
        => (await _http.DeleteAsync($"v1/job/{Uri.EscapeDataString(jobName)}?purge={purge}", ct)).EnsureSuccessStatusCode();

    public async Task<IReadOnlyList<string>> ValidateJobAsync(string hclPath, CancellationToken ct = default)
    {
        var hcl = await File.ReadAllTextAsync(hclPath, ct);
        var parseResp = await _http.PostAsJsonAsync("v1/jobs/parse", new { JobHCL = hcl, Canonicalize = true }, ct);
        if (!parseResp.IsSuccessStatusCode)
        {
	        return [$"Parse error: {await parseResp.Content.ReadAsStringAsync(ct)}"];
        }

        var jobJson = await parseResp.Content.ReadAsStringAsync(ct);
        var valResp = await _http.PostAsync("v1/validate/job",
            new StringContent($"{{\"Job\":{jobJson}}}", Encoding.UTF8, "application/json"), ct);
        valResp.EnsureSuccessStatusCode();
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

    private sealed class NomadJobStub
    {
        [JsonPropertyName("ID")] public string Id { get; } = string.Empty;
    }
}
