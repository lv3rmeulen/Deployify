using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.SelfUpdate;

/// <summary>
/// Lists the published versions of a package from a NuGet feed: a v3 feed's service index
/// (https://api.nuget.org/v3/index.json by default), or a local folder of .nupkg files.
/// </summary>
internal sealed class PackageVersionSource(HttpClient? http = null)
{
    public const string DefaultSource = "https://api.nuget.org/v3/index.json";

    /// <summary>Overrides <see cref="DefaultSource"/>, like <c>--source</c>.</summary>
    public const string SourceVariable = "DEPLOYIFY_UPDATE_SOURCE";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http = http ?? s_http;

    public static string Resolve(string? source)
        => !string.IsNullOrWhiteSpace(source) ? source
            : Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } fromEnv ? fromEnv
            : DefaultSource;

    /// <summary>Every version of <paramref name="packageId"/> in <paramref name="source"/>, unlisted ones included; empty when it has none.</summary>
    /// <exception cref="SelfUpdateException">The feed couldn't be read.</exception>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string source, string packageId, CancellationToken ct)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return FromFolder(uri?.LocalPath ?? source, packageId);
        }

        try
        {
            var baseAddress = await PackageBaseAddressAsync(uri, ct);
            using var response = await _http.GetAsync(new Uri(baseAddress, $"{packageId.ToLowerInvariant()}/index.json"), ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return doc.RootElement.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array
                ? [.. versions.EnumerateArray().Select(v => v.GetString()).OfType<string>()]
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SelfUpdateException($"Couldn't read the versions of {packageId} from {source}: {ex.Message}", ex);
        }
    }

    /// <summary>The feed's flat container ("PackageBaseAddress/3.0.0"), with a trailing slash.</summary>
    private async Task<Uri> PackageBaseAddressAsync(Uri serviceIndex, CancellationToken ct)
    {
        using var response = await _http.GetAsync(serviceIndex, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (doc.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.TryGetProperty("@type", out var type) && type.GetString() is { } t &&
                    t.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal) &&
                    resource.TryGetProperty("@id", out var id) && id.GetString() is { } address)
                {
                    return new Uri(address.EndsWith('/') ? address : address + "/");
                }
            }
        }

        throw new SelfUpdateException($"{serviceIndex} isn't a NuGet v3 feed: its service index has no PackageBaseAddress resource.");
    }

    private static List<string> FromFolder(string folder, string packageId)
    {
        if (!Directory.Exists(folder))
        {
            throw new SelfUpdateException($"The package source {folder} is neither an http(s) feed nor a folder.");
        }

        var prefix = packageId + ".";
        return [.. Directory.EnumerateFiles(folder, "*.nupkg")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".symbols", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[prefix.Length..])
            .Where(v => v.Length > 0 && char.IsAsciiDigit(v[0]))];
    }
}

internal sealed class SelfUpdateException(string message, Exception? inner = null) : Exception(message, inner);
