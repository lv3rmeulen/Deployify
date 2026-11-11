using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Target.OvhCloud;

/// <summary>
/// The OVHcloud API credentials for one environment: either an API application (application
/// key, application secret, consumer key; requests are signed) or a service account (OAuth2
/// client credentials; requests carry a bearer token).
/// </summary>
internal sealed record OvhCredentials(
    string? ApplicationKey, string? ApplicationSecret, string? ConsumerKey,
    string? ClientId, string? ClientSecret)
{
    public bool IsServiceAccount => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    public bool IsApplication =>
        !string.IsNullOrEmpty(ApplicationKey) && !string.IsNullOrEmpty(ApplicationSecret) && !string.IsNullOrEmpty(ConsumerKey);

    public bool IsComplete => IsServiceAccount || IsApplication;

    /// <summary>Reads the configured values, falling back to the OVH_* environment variables.</summary>
    public static OvhCredentials From(OvhCloudTargetConfig cfg) => new(
        Secrets.Resolve(cfg.ApplicationKey, "OVH_APPLICATION_KEY"),
        Secrets.Resolve(cfg.ApplicationSecret, "OVH_APPLICATION_SECRET"),
        Secrets.Resolve(cfg.ConsumerKey, "OVH_CONSUMER_KEY"),
        Secrets.Resolve(cfg.ClientId, "OVH_CLIENT_ID"),
        Secrets.Resolve(cfg.ClientSecret, "OVH_CLIENT_SECRET"));

    /// <summary>Never prints a secret.</summary>
    public override string ToString()
        => IsServiceAccount ? $"service account {ClientId}"
            : IsApplication ? $"application {ApplicationKey}"
            : "no credentials";
}

/// <summary>An OVHcloud API call that failed, with the API's own message.</summary>
public sealed class OvhApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Thin client for the OVHcloud API (v1). Signs requests for API applications as the official
/// SDKs do (<c>"$1$" + SHA1(secret+consumerKey+method+url+body+timestamp)</c>, with the clock
/// taken from <c>/auth/time</c>), or fetches and caches an OAuth2 token for a service account.
/// </summary>
internal sealed class OvhApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Uri _tokenUri;
    private readonly OvhCredentials _credentials;
    private readonly TimeProvider _time;

    private long? _clockDelta;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public OvhApiClient(string? endpoint, OvhCredentials credentials, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        if (!credentials.IsComplete)
        {
            throw new InvalidOperationException(
                "No OVHcloud API credentials. Set OVH_APPLICATION_KEY, OVH_APPLICATION_SECRET and OVH_CONSUMER_KEY " +
                "(an API application), or OVH_CLIENT_ID and OVH_CLIENT_SECRET (a service account); or set them in " +
                "targetConfig.ovhcloud as \"env:NAME\" references.");
        }

        (_baseUri, _tokenUri) = ResolveEndpoint(endpoint);
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// API base URL and OAuth2 token URL for an endpoint name (<c>ovh-eu</c>, <c>ovh-ca</c>,
    /// <c>ovh-us</c>; default <c>ovh-eu</c>, or OVH_ENDPOINT when unset) or a full API URL.
    /// </summary>
    internal static (Uri Api, Uri Token) ResolveEndpoint(string? endpoint)
    {
        var name = string.IsNullOrWhiteSpace(endpoint)
            ? Environment.GetEnvironmentVariable("OVH_ENDPOINT")
            : endpoint;
        name = string.IsNullOrWhiteSpace(name) ? "ovh-eu" : name.Trim();

        switch (name.ToLowerInvariant())
        {
            case "ovh-eu":
                return (new Uri("https://eu.api.ovh.com/1.0/"), new Uri("https://www.ovh.com/auth/oauth2/token"));
            case "ovh-ca":
                return (new Uri("https://ca.api.ovh.com/1.0/"), new Uri("https://ca.ovh.com/auth/oauth2/token"));
            case "ovh-us":
                return (new Uri("https://api.us.ovhcloud.com/1.0/"), new Uri("https://us.ovhcloud.com/auth/oauth2/token"));
        }

        if (Uri.TryCreate(name, UriKind.Absolute, out var custom) && custom.Scheme == Uri.UriSchemeHttps)
        {
            var api = new Uri(custom.AbsoluteUri.TrimEnd('/') + "/");
            var host = api.Host;
            var token = host.Contains("ca.api.ovh.com", StringComparison.OrdinalIgnoreCase) ? "https://ca.ovh.com/auth/oauth2/token"
                : host.Contains("ovhcloud.com", StringComparison.OrdinalIgnoreCase) ? "https://us.ovhcloud.com/auth/oauth2/token"
                : "https://www.ovh.com/auth/oauth2/token";
            return (api, new Uri(token));
        }

        throw new InvalidOperationException(
            $"Unknown OVHcloud endpoint '{name}'. Use ovh-eu, ovh-ca, ovh-us, or an https API URL.");
    }

    // ── Calls ─────────────────────────────────────────────────────────────────

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default)
        => SendAsync<T>(HttpMethod.Get, path, body: null, ct);

    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
        => SendAsync<T>(HttpMethod.Post, path, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = new Uri(_baseUri, path.TrimStart('/'));
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body, s_json);

        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_credentials.IsServiceAccount)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        }
        else
        {
            var timestamp = (_time.GetUtcNow().ToUnixTimeSeconds() + await ClockDeltaAsync(ct))
                .ToString(CultureInfo.InvariantCulture);
            request.Headers.Add("X-Ovh-Application", _credentials.ApplicationKey);
            request.Headers.Add("X-Ovh-Consumer", _credentials.ConsumerKey);
            request.Headers.Add("X-Ovh-Timestamp", timestamp);
            request.Headers.Add("X-Ovh-Signature", Sign(
                _credentials.ApplicationSecret!, _credentials.ConsumerKey!, method.Method, url.AbsoluteUri, json, timestamp));
        }

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new OvhApiException(response.StatusCode,
                $"OVHcloud API {method.Method} /{path.TrimStart('/')} failed ({(int)response.StatusCode}): {ErrorMessage(text)}");
        }

        return JsonSerializer.Deserialize<T>(text, s_json)
               ?? throw new OvhApiException(response.StatusCode, $"OVHcloud API {method.Method} /{path.TrimStart('/')} returned no data.");
    }

    /// <summary>The request signature for an API application.</summary>
    internal static string Sign(string applicationSecret, string consumerKey, string method, string url, string body, string timestamp)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{applicationSecret}+{consumerKey}+{method}+{url}+{body}+{timestamp}"));
        return "$1$" + Convert.ToHexStringLower(hash);
    }

    /// <summary>Seconds between the API's clock and ours; signed requests must use the API's time.</summary>
    private async Task<long> ClockDeltaAsync(CancellationToken ct)
    {
        if (_clockDelta is { } delta)
        {
            return delta;
        }

        using var response = await _http.GetAsync(new Uri(_baseUri, "auth/time"), ct);
        response.EnsureSuccessStatusCode();
        var serverTime = long.Parse((await response.Content.ReadAsStringAsync(ct)).Trim(), CultureInfo.InvariantCulture);
        _clockDelta = serverTime - _time.GetUtcNow().ToUnixTimeSeconds();
        return _clockDelta.Value;
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && _time.GetUtcNow() < _accessTokenExpires)
        {
            return _accessToken;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _credentials.ClientId!,
            ["client_secret"] = _credentials.ClientSecret!,
            ["scope"] = "all"
        });
        using var response = await _http.PostAsync(_tokenUri, form, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new OvhApiException(response.StatusCode,
                $"Could not get an OVHcloud access token for {_credentials} ({(int)response.StatusCode}): {ErrorMessage(text)}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(text, s_json)
                    ?? throw new OvhApiException(response.StatusCode, "The OVHcloud token endpoint returned no token.");
        _accessToken = token.AccessToken;
        // Renew a minute early so a token never expires mid-run.
        _accessTokenExpires = _time.GetUtcNow().AddSeconds(Math.Max(0, token.ExpiresIn - 60));
        return _accessToken;
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "message", "error_description", "error" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.String)
                {
                    return m.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body) ? "(no message)" : body.Trim();
    }

    public void Dispose() => _http.Dispose();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
