using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.DigitalOcean;

/// <summary>An app as the API returns it.</summary>
public sealed record DoApp(string Id, string Name, JsonObject Spec, string? LiveUrl, string? PendingDeploymentId);

/// <summary>A deployment of an app. Phase is ACTIVE when it went live, ERROR or CANCELED when it didn't.</summary>
public sealed record DoDeployment(string Id, string Phase, string? Cause = null)
{
    public bool IsFinished => Phase is "ACTIVE" or "SUPERSEDED" or "ERROR" or "CANCELED";

    public bool Succeeded => Phase is "ACTIVE" or "SUPERSEDED";
}

/// <summary>What <c>/v2/apps/propose</c> says about a spec. The cost is US dollars per month.</summary>
public sealed record DoProposal(decimal? AppCost, bool? NameAvailable, string? NameSuggestion = null);

public sealed record DoCluster(string Id, string Name, string Engine, string Status, string? Region, IReadOnlyList<string> Tags);

public sealed record DoClusterRequest(string Name, string Engine, string? Version, string Region, string Size, int Nodes, IReadOnlyList<string> Tags);

/// <summary>An error the API returned: <see cref="StatusCode"/> and DigitalOcean's own message.</summary>
public sealed class DoApiException(HttpStatusCode statusCode, string? errorId, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ErrorId { get; } = errorId;

    /// <summary>True when DigitalOcean rejected the request itself (a bad spec), not when it couldn't be asked.</summary>
    public bool IsRejection => (int)StatusCode is >= 400 and < 500 and not 401 and not 403 and not 429;
}

/// <summary>The DigitalOcean API calls the target makes. Tests use a fake.</summary>
public interface IDigitalOceanApi
{
    /// <summary>Null when the token works; otherwise why not.</summary>
    Task<string?> PingAsync(CancellationToken ct);

    Task<DoApp?> FindAppAsync(string name, CancellationToken ct);

    Task<DoApp> CreateAppAsync(JsonObject spec, string? projectId, CancellationToken ct);

    Task<DoApp> UpdateAppAsync(string appId, JsonObject spec, CancellationToken ct);

    Task DeleteAppAsync(string appId, CancellationToken ct);

    Task<DoProposal> ProposeAsync(JsonObject spec, string? appId, CancellationToken ct);

    Task<DoDeployment?> GetDeploymentAsync(string appId, string deploymentId, CancellationToken ct);

    Task<DoDeployment?> GetLatestDeploymentAsync(string appId, CancellationToken ct);

    Task<DoCluster?> FindClusterAsync(string name, CancellationToken ct);

    Task<DoCluster?> GetClusterAsync(string id, CancellationToken ct);

    /// <summary>The clusters that carry <paramref name="tag"/>.</summary>
    Task<IReadOnlyList<DoCluster>> ListClustersAsync(string tag, CancellationToken ct);

    Task<DoCluster> CreateClusterAsync(DoClusterRequest request, CancellationToken ct);

    Task DeleteClusterAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<string>> ListClusterDatabasesAsync(string clusterId, CancellationToken ct);

    Task CreateClusterDatabaseAsync(string clusterId, string name, CancellationToken ct);

    /// <summary>Adds the app to the cluster's trusted sources, keeping the rules already there.</summary>
    Task AllowAppAsync(string clusterId, string appId, CancellationToken ct);

    /// <summary>
    /// Where a component's run logs can be read (GET /v2/apps/{id}/components/{name}/logs?type=RUN):
    /// the live URL and archived ones. The URLs carry their own access token.
    /// </summary>
    Task<DoLogUrls> GetRunLogUrlsAsync(string appId, string component, bool follow, int? tailLines, CancellationToken ct)
        => throw new NotSupportedException("This DigitalOcean API client can't read logs.");

    /// <summary>
    /// The lines behind a log URL: an https URL is read to its end (or, for a live URL that
    /// follows, until cancelled); a wss URL streams until cancelled.
    /// </summary>
    IAsyncEnumerable<string> ReadLogLinesAsync(string url, CancellationToken ct)
        => throw new NotSupportedException("This DigitalOcean API client can't read logs.");
}

/// <summary>Where App Platform serves a component's logs.</summary>
public sealed record DoLogUrls(string? LiveUrl, IReadOnlyList<string> HistoricUrls);

/// <summary><see cref="IDigitalOceanApi"/> over HTTPS, authenticated with a personal access token.</summary>
public sealed class DigitalOceanApi : IDigitalOceanApi, IDisposable
{
    public static readonly Uri DefaultBaseAddress = new("https://api.digitalocean.com/");

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http;

    public DigitalOceanApi(string token, HttpMessageHandler? handler = null, Uri? baseAddress = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseAddress ?? DefaultBaseAddress;
        _http.Timeout = TimeSpan.FromSeconds(100);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("deployify-target-digitalocean", "1.0"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _logs.Dispose();
    }

    public async Task<string?> PingAsync(CancellationToken ct)
    {
        try
        {
            var account = await SendAsync(HttpMethod.Get, "v2/account", null, ct);
            return account?["account"]?["status"]?.GetValue<string>() is "locked"
                ? "The DigitalOcean account is locked."
                : null;
        }
        catch (DoApiException ex)
        {
            return ex.StatusCode == HttpStatusCode.Unauthorized
                ? "DigitalOcean rejected the API token (401). Check the token and that it hasn't expired."
                : ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return $"Couldn't reach the DigitalOcean API: {ex.Message}";
        }
    }

    public async Task<DoApp?> FindAppAsync(string name, CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            var result = await SendAsync(HttpMethod.Get, $"v2/apps?page={page}&per_page=100", null, ct);
            var apps = result?["apps"]?.AsArray() ?? [];
            foreach (var app in apps)
            {
                if (app?["spec"]?["name"]?.GetValue<string>() == name)
                {
                    return ReadApp(app);
                }
            }

            if (apps.Count < 100 || result?["links"]?["pages"]?["next"] is null)
            {
                return null;
            }
        }
    }

    public async Task<DoApp> CreateAppAsync(JsonObject spec, string? projectId, CancellationToken ct)
    {
        var body = new JsonObject { ["spec"] = spec.DeepClone() };
        if (projectId is not null)
        {
            body["project_id"] = projectId;
        }

        var result = await SendAsync(HttpMethod.Post, "v2/apps", body, ct);
        return ReadApp(result?["app"]) ?? throw new InvalidOperationException("DigitalOcean created the app but returned no app.");
    }

    public async Task<DoApp> UpdateAppAsync(string appId, JsonObject spec, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Put, $"v2/apps/{appId}", new JsonObject { ["spec"] = spec.DeepClone() }, ct);
        return ReadApp(result?["app"]) ?? throw new InvalidOperationException("DigitalOcean updated the app but returned no app.");
    }

    public async Task DeleteAppAsync(string appId, CancellationToken ct)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"v2/apps/{appId}", null, ct);
        }
        catch (DoApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<DoProposal> ProposeAsync(JsonObject spec, string? appId, CancellationToken ct)
    {
        var body = new JsonObject { ["spec"] = spec.DeepClone() };
        if (appId is not null)
        {
            body["app_id"] = appId;
        }

        var result = await SendAsync(HttpMethod.Post, "v2/apps/propose", body, ct);
        return new DoProposal(
            Decimal(result?["app_cost"]),
            result?["app_name_available"]?.GetValue<bool>(),
            result?["app_name_suggestion"]?.GetValue<string>());
    }

    public async Task<DoDeployment?> GetDeploymentAsync(string appId, string deploymentId, CancellationToken ct)
    {
        try
        {
            return ReadDeployment((await SendAsync(HttpMethod.Get, $"v2/apps/{appId}/deployments/{deploymentId}", null, ct))?["deployment"]);
        }
        catch (DoApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<DoDeployment?> GetLatestDeploymentAsync(string appId, CancellationToken ct)
        => ReadDeployment((await SendAsync(HttpMethod.Get, $"v2/apps/{appId}/deployments?page=1&per_page=1", null, ct))?["deployments"]?.AsArray().FirstOrDefault());

    public async Task<DoCluster?> FindClusterAsync(string name, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, "v2/databases", null, ct);
        return (result?["databases"]?.AsArray() ?? []).Select(ReadCluster).FirstOrDefault(c => c?.Name == name);
    }

    public async Task<DoCluster?> GetClusterAsync(string id, CancellationToken ct)
    {
        try
        {
            return ReadCluster((await SendAsync(HttpMethod.Get, $"v2/databases/{id}", null, ct))?["database"]);
        }
        catch (DoApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DoCluster>> ListClustersAsync(string tag, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"v2/databases?tag_name={Uri.EscapeDataString(tag)}", null, ct);
        return [.. (result?["databases"]?.AsArray() ?? []).Select(ReadCluster).OfType<DoCluster>()];
    }

    public async Task<DoCluster> CreateClusterAsync(DoClusterRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = request.Name,
            ["engine"] = request.Engine,
            ["region"] = request.Region,
            ["size"] = request.Size,
            ["num_nodes"] = request.Nodes,
            ["tags"] = new JsonArray([.. request.Tags.Select(t => (JsonNode)JsonValue.Create(t))])
        };
        if (request.Version is not null)
        {
            body["version"] = request.Version;
        }

        var result = await SendAsync(HttpMethod.Post, "v2/databases", body, ct);
        return ReadCluster(result?["database"]) ?? throw new InvalidOperationException("DigitalOcean created the cluster but returned none.");
    }

    public async Task DeleteClusterAsync(string id, CancellationToken ct)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"v2/databases/{id}", null, ct);
        }
        catch (DoApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<IReadOnlyList<string>> ListClusterDatabasesAsync(string clusterId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"v2/databases/{clusterId}/dbs", null, ct);
        return [.. (result?["dbs"]?.AsArray() ?? []).Select(d => d?["name"]?.GetValue<string>()).OfType<string>()];
    }

    public Task CreateClusterDatabaseAsync(string clusterId, string name, CancellationToken ct)
        => SendAsync(HttpMethod.Post, $"v2/databases/{clusterId}/dbs", new JsonObject { ["name"] = name }, ct);

    public async Task AllowAppAsync(string clusterId, string appId, CancellationToken ct)
    {
        var current = await SendAsync(HttpMethod.Get, $"v2/databases/{clusterId}/firewall", null, ct);
        var rules = (current?["rules"]?.AsArray() ?? [])
            .Select(r => (Type: r?["type"]?.GetValue<string>(), Value: r?["value"]?.GetValue<string>()))
            .Where(r => r.Type is not null && r.Value is not null)
            .ToList();
        if (rules.Any(r => r.Type == "app" && r.Value == appId))
        {
            return;
        }

        rules.Add(("app", appId));
        var body = new JsonObject
        {
            ["rules"] = new JsonArray([.. rules.Select(r => (JsonNode)new JsonObject { ["type"] = r.Type, ["value"] = r.Value })])
        };
        await SendAsync(HttpMethod.Put, $"v2/databases/{clusterId}/firewall", body, ct);
    }

    // ── Logs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// For the log URLs, which App Platform signs itself: never send them the API token, which
    /// belongs to api.digitalocean.com only.
    /// </summary>
    private readonly HttpClient _logs = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<DoLogUrls> GetRunLogUrlsAsync(string appId, string component, bool follow, int? tailLines, CancellationToken ct)
    {
        var query = $"type=RUN&follow={(follow ? "true" : "false")}&pod_connection_timeout=30s" +
                    (tailLines is { } n ? $"&tail_lines={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "");
        var result = await SendAsync(HttpMethod.Get,
            $"v2/apps/{Uri.EscapeDataString(appId)}/components/{Uri.EscapeDataString(component)}/logs?{query}", null, ct);
        return new DoLogUrls(
            result?["live_url"]?.GetValue<string>() is { Length: > 0 } live ? live : null,
            [.. (result?["historic_urls"]?.AsArray() ?? []).Select(u => u?.GetValue<string>()).OfType<string>()]);
    }

    public async IAsyncEnumerable<string> ReadLogLinesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme is "wss" or "ws")
        {
            await foreach (var line in ReadWebSocketAsync(uri, ct))
            {
                yield return line;
            }

            yield break;
        }

        using var response = await _logs.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new DoApiException(response.StatusCode, null,
                $"DigitalOcean's log service answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            yield return line;
        }
    }

    /// <summary>A live log over a WebSocket: each message is <c>{"data": "..."}</c> (or plain text) holding one or more lines.</summary>
    private static async IAsyncEnumerable<string> ReadWebSocketAsync(Uri uri, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        var token = System.Web.HttpUtility.ParseQueryString(uri.Query)["token"];
        if (!string.IsNullOrEmpty(token))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        await socket.ConnectAsync(uri, ct);
        var buffer = new byte[64 * 1024];
        var message = new System.IO.MemoryStream();
        while (socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, ct);
            if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                yield break;
            }

            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            foreach (var line in LogMessageLines(text))
            {
                yield return line;
            }
        }
    }

    internal static IEnumerable<string> LogMessageLines(string message)
    {
        var text = message;
        if (message.TrimStart().StartsWith('{'))
        {
            try
            {
                text = JsonNode.Parse(message)?["data"]?.GetValue<string>() ?? message;
            }
            catch (JsonException)
            {
            }
        }

        return text.TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r'));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(s_json), Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            // Rate limited: wait as long as DigitalOcean asks (capped) and try again, a few times.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                string? id = null;
                var message = text;
                try
                {
                    var error = JsonNode.Parse(text);
                    id = error?["id"]?.GetValue<string>();
                    message = error?["message"]?.GetValue<string>() ?? text;
                }
                catch (JsonException)
                {
                }

                throw new DoApiException(response.StatusCode, id,
                    $"DigitalOcean API {method} /{path.Split('?')[0]} failed ({(int)response.StatusCode}): {message}");
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private static DoApp? ReadApp(JsonNode? app)
        => app?["id"]?.GetValue<string>() is not { } id
            ? null
            : new DoApp(
                id,
                app["spec"]?["name"]?.GetValue<string>() ?? "",
                app["spec"]?.DeepClone().AsObject() ?? [],
                app["live_url"]?.GetValue<string>(),
                app["pending_deployment"]?["id"]?.GetValue<string>() ?? app["in_progress_deployment"]?["id"]?.GetValue<string>());

    private static DoDeployment? ReadDeployment(JsonNode? deployment)
        => deployment?["id"]?.GetValue<string>() is not { } id
            ? null
            : new DoDeployment(id, deployment["phase"]?.GetValue<string>() ?? "UNKNOWN", deployment["cause"]?.GetValue<string>());

    private static DoCluster? ReadCluster(JsonNode? db)
        => db?["id"]?.GetValue<string>() is not { } id
            ? null
            : new DoCluster(
                id,
                db["name"]?.GetValue<string>() ?? "",
                db["engine"]?.GetValue<string>() ?? "",
                db["status"]?.GetValue<string>() ?? "",
                db["region"]?.GetValue<string>(),
                [.. (db["tags"]?.AsArray() ?? []).Select(t => t?.GetValue<string>()).OfType<string>()]);

    private static decimal? Decimal(JsonNode? node)
        => node is JsonValue v && (v.TryGetValue<decimal>(out var d) || (v.TryGetValue<string>(out var s) && decimal.TryParse(
            s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d)))
            ? d
            : null;
}
