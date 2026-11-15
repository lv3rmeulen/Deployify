using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Tests.Shared;

/// <summary>
/// Checks with a real Nomad agent that a JSON job file describes the same job as the HCL file it
/// replaced: the HCL goes through Nomad's own parser and is registered, then the JSON job is
/// planned against it, and Nomad must report no change. Runs only when
/// <c>DEPLOYIFY_TEST_NOMAD_ADDR</c> is set (for example to a <c>nomad agent -dev</c>, or
/// <c>-dev-connect</c> for Consul Connect jobs).
/// </summary>
internal static class NomadEquivalence
{
    public const string AddressVariable = "DEPLOYIFY_TEST_NOMAD_ADDR";

    public static string? Address => Environment.GetEnvironmentVariable(AddressVariable) is { Length: > 0 } a ? a.TrimEnd('/') : null;

    /// <summary>
    /// Returns null when Nomad sees no difference, otherwise Nomad's diff. Both jobs are moved to
    /// the default namespace under a test ID, so the agent needs nothing set up, and purged after.
    /// </summary>
    public static async Task<string?> DiffAsync(string address, string hclPath, string jsonPath, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address + "/") };
        var id = "deployify-eq-" + Guid.NewGuid().ToString("N")[..8];

        var parse = await PostAsync(http, "v1/jobs/parse",
            new JsonObject { ["JobHCL"] = await File.ReadAllTextAsync(hclPath, ct), ["Canonicalize"] = true }, ct);
        var fromHcl = Rename(parse.AsObject(), id);

        var fromJson = Rename(JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct))!["Job"]!.DeepClone().AsObject(), id);

        try
        {
            await PostAsync(http, "v1/jobs", new JsonObject { ["Job"] = fromHcl }, ct);
            var plan = await PostAsync(http, $"v1/job/{id}/plan", new JsonObject { ["Job"] = fromJson, ["Diff"] = true }, ct);
            var diff = plan["Diff"];
            return (string?)diff?["Type"] is null or "None" ? null : diff!.ToJsonString(new() { WriteIndented = true });
        }
        finally
        {
            await http.DeleteAsync($"v1/job/{id}?purge=true", ct);
        }
    }

    private static JsonObject Rename(JsonObject job, string id)
    {
        job["ID"] = id;
        job["Name"] = id;
        job["Namespace"] = "default";
        return job;
    }

    private static async Task<JsonNode> PostAsync(HttpClient http, string path, JsonObject body, CancellationToken ct)
    {
        using var resp = await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nomad {path} failed ({(int)resp.StatusCode}): {text}");
        }

        return JsonNode.Parse(text)!;
    }
}
