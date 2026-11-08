using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Deployify.Core.Models;
using Deployify.Core.Processes;

namespace Deployify.Core.State;

/// <summary>
/// An Azure Storage blob container, through the Blob REST API. The version tag is the blob's
/// ETag; writes send <c>If-Match</c> (or <c>If-None-Match: *</c> to create), so Azure rejects a
/// conflicting write with 412/409. Authenticates with a SAS token, or else with an Entra token
/// for the signed-in Azure CLI user.
/// </summary>
public sealed class AzureBlobStateBackend : IStateBackend, IDisposable
{
    private const string ApiVersion = "2023-11-03";

    private readonly HttpClient _http;
    private readonly string _container;
    private readonly string? _sas;
    private readonly IProcessRunner _processes;
    private string? _bearer;
    private DateTimeOffset _bearerExpires;

    public AzureBlobStateBackend(AzureBlobStateConfig config, HttpMessageHandler? handler = null, IProcessRunner? processes = null)
    {
        if (string.IsNullOrWhiteSpace(config.AccountName) && string.IsNullOrWhiteSpace(config.Endpoint))
        {
            throw new StateBackendException("state.azureBlob.accountName is required for the AzureBlob state backend.");
        }

        var endpoint = (config.Endpoint ?? $"https://{config.AccountName}.blob.core.windows.net").TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(endpoint + "/");
        _container = config.Container;
        _sas = Secrets.Resolve(config.SasToken, "DEPLOYIFY_STATE_SAS_TOKEN")?.TrimStart('?');
        _processes = processes ?? ProcessRunner.Default;
        Description = $"{endpoint}/{config.Container}";
    }

    public string Description { get; }

    public async Task<StateBlob?> ReadAsync(string key, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, BlobUrl(key), null, null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(resp, $"read {key}", ct);
        return new StateBlob(await resp.Content.ReadAsByteArrayAsync(ct), ETagOf(resp));
    }

    public async Task<string> WriteAsync(string key, byte[] data, string? expectedETag, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Put, BlobUrl(key), data, req =>
        {
            req.Headers.Add("x-ms-blob-type", "BlockBlob");
            if (expectedETag is null)
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            }
            else
            {
                req.Headers.TryAddWithoutValidation("If-Match", expectedETag);
            }
        }, ct);

        if (resp.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new StateConflictException(key, expectedETag is null ? "it already exists" : null);
        }

        await EnsureAsync(resp, $"write {key}", ct);
        return ETagOf(resp);
    }

    public async Task DeleteAsync(string key, string? expectedETag, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Delete, BlobUrl(key), null, req =>
        {
            if (expectedETag is not null)
            {
                req.Headers.TryAddWithoutValidation("If-Match", expectedETag);
            }
        }, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new StateConflictException(key);
        }

        await EnsureAsync(resp, $"delete {key}", ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<string>();
        string? marker = null;
        do
        {
            var url = $"{Uri.EscapeDataString(_container)}?restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}" +
                      (marker is null ? "" : $"&marker={Uri.EscapeDataString(marker)}");
            using var resp = await SendAsync(HttpMethod.Get, url, null, null, ct);
            await EnsureAsync(resp, $"list {prefix}", ct);
            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            keys.AddRange(xml.Descendants("Blob").Select(b => b.Element("Name")?.Value).OfType<string>());
            marker = xml.Root?.Element("NextMarker")?.Value;
        }
        while (!string.IsNullOrEmpty(marker));

        return [.. keys.Order(StringComparer.Ordinal)];
    }

    public void Dispose() => _http.Dispose();

    private string BlobUrl(string key)
        => $"{Uri.EscapeDataString(_container)}/{string.Join('/', key.Split('/').Select(Uri.EscapeDataString))}";

    private static string ETagOf(HttpResponseMessage resp)
        => resp.Headers.ETag?.Tag
           ?? (resp.Headers.TryGetValues("ETag", out var v) ? v.First() : throw new StateBackendException("Azure returned no ETag."));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, byte[]? body, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        if (_sas is not null)
        {
            url += (url.Contains('?') ? "&" : "?") + _sas;
        }

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("x-ms-version", ApiVersion);
        if (_sas is null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await BearerAsync(ct));
        }

        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        configure?.Invoke(req);
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StateBackendException($"Cannot reach {Description}: {ex.Message}", ex);
        }
    }

    /// <summary>An Entra token for Azure Storage from the Azure CLI, reused until shortly before it expires.</summary>
    private async Task<string> BearerAsync(CancellationToken ct)
    {
        if (_bearer is not null && DateTimeOffset.UtcNow < _bearerExpires)
        {
            return _bearer;
        }

        ProcessResult result;
        try
        {
            result = await _processes.RunAsync(new ProcessSpec("az",
                ["account", "get-access-token", "--resource", "https://storage.azure.com/", "--query", "accessToken", "--output", "tsv"])
            {
                Sensitive = true
            }, ct);
        }
        catch (ToolNotFoundException ex)
        {
            throw new StateBackendException(
                "The AzureBlob state backend needs the Azure CLI (az) to sign in, or a SAS token in state.azureBlob.sasToken.", ex);
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Stdout))
        {
            throw new StateBackendException($"Could not get an Azure Storage token from the Azure CLI: {result.ErrorOrOutput.Trim()}");
        }

        _bearer = result.Stdout.Trim();
        _bearerExpires = DateTimeOffset.UtcNow.AddMinutes(30);
        return _bearer;
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new StateBackendException($"Azure Blob state {action} failed ({(int)resp.StatusCode}): {body.Trim()}");
        }
    }
}
