using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Plugins;

/// <summary>Where a plugin index is, and who must have signed it.</summary>
public sealed record PluginIndexSource(string Url, PluginSigner Signer)
{
    /// <summary>The public index, published with the docs.</summary>
    public const string DefaultUrl = "https://lv3rmeulen.github.io/Deployify/plugins/index.json";

    /// <summary>
    /// The workflow that builds and signs the public index. Only that workflow file, run from
    /// main, can make a signature with this identity.
    /// </summary>
    public const string DefaultIdentity =
        "https://github.com/lv3rmeulen/Deployify/.github/workflows/plugin-index.yml@refs/heads/main";

    /// <summary>Environment variables that point Deployify at another index, such as a company's own.</summary>
    public const string UrlVariable = "DEPLOYIFY_PLUGIN_INDEX";
    public const string IdentityVariable = "DEPLOYIFY_PLUGIN_INDEX_IDENTITY";
    public const string IssuerVariable = "DEPLOYIFY_PLUGIN_INDEX_ISSUER";
    public const string KeyVariable = "DEPLOYIFY_PLUGIN_INDEX_KEY";

    public static PluginIndexSource Default { get; } =
        new(DefaultUrl, new PluginSigner { Identity = DefaultIdentity, Issuer = PluginSigner.GitHubActionsIssuer });

    public bool IsDefault => string.Equals(Url, DefaultUrl, StringComparison.Ordinal);

    /// <summary>
    /// The index to use: <paramref name="url"/> (or <c>DEPLOYIFY_PLUGIN_INDEX</c>), trusted as
    /// signed by <paramref name="identity"/> and <paramref name="issuer"/>, or by the public key
    /// in file <paramref name="keyFile"/> (each falling back to its environment variable). The
    /// public index needs none of them. Another index must say who signs it.
    /// </summary>
    public static PluginIndexSource Resolve(
        string? url, string? identity, string? issuer, string? keyFile, Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        url = Blank(url) ?? Blank(getVariable(UrlVariable));
        identity = Blank(identity) ?? Blank(getVariable(IdentityVariable));
        issuer = Blank(issuer) ?? Blank(getVariable(IssuerVariable));
        keyFile = Blank(keyFile) ?? Blank(getVariable(KeyVariable));

        if (url is null || string.Equals(url, DefaultUrl, StringComparison.Ordinal))
        {
            if (identity is not null || issuer is not null || keyFile is not null)
            {
                throw new PluginException("The public plugin index's signer is built in; --index-identity, --index-issuer and --index-key are for another index (--index).");
            }

            return Default;
        }

        PluginSigner signer;
        if (keyFile is not null)
        {
            if (identity is not null || issuer is not null)
            {
                throw new PluginException("Give the plugin index's signer as a public key (--index-key) or a keyless identity (--index-identity, --index-issuer), not both.");
            }

            if (!File.Exists(keyFile))
            {
                throw new PluginException($"The plugin index key {keyFile} doesn't exist.");
            }

            signer = new PluginSigner { PublicKey = File.ReadAllText(keyFile) };
        }
        else if (identity is not null)
        {
            signer = new PluginSigner { Identity = identity, Issuer = issuer ?? PluginSigner.GitHubActionsIssuer };
        }
        else
        {
            throw new PluginException(
                $"Say who signs the plugin index {url}: --index-identity (with --index-issuer unless it's signed in GitHub Actions) " +
                $"or --index-key, or {IdentityVariable} / {KeyVariable}. Deployify doesn't read unsigned indexes.");
        }

        var problems = string.Join(" ", signer.Validate());
        return problems.Length > 0
            ? throw new PluginException($"The plugin index signer is incomplete: {problems}")
            : new PluginIndexSource(url, signer);

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary><paramref name="reference"/> resolved against the index's location, so packages can be listed relative to it.</summary>
    public string Resolve(string reference) => PluginFetcher.Combine(Url, reference);
}

/// <summary>Downloads index files and packages: <c>https://</c> URLs, <c>file://</c> URLs and local paths.</summary>
public class PluginFetcher
{
    /// <summary>The largest file Deployify downloads: no plugin package needs more.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Deployify/{typeof(PluginFetcher).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
        return client;
    });

    /// <summary>Downloads <paramref name="location"/> to <paramref name="destination"/>.</summary>
    public virtual async Task DownloadAsync(string location, string destination, CancellationToken ct = default)
    {
        if (LocalPath(location) is { } local)
        {
            if (!File.Exists(local))
            {
                throw new PluginException($"{location} doesn't exist.");
            }

            if (new FileInfo(local).Length > MaxBytes)
            {
                throw new PluginException($"{location} is larger than {MaxBytes / (1024 * 1024)} MB.");
            }

            File.Copy(local, destination, overwrite: true);
            return;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            // Plain http would let anyone on the path swap the file; the signature would catch it,
            // but an index that can't even be fetched safely is a mistake worth reporting.
            throw new PluginException($"{location} isn't an https:// URL, a file:// URL or a local path.");
        }

        try
        {
            using var response = await s_http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new PluginException($"Downloading {location} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new PluginException($"{location} is larger than {MaxBytes / (1024 * 1024)} MB.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(destination);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    throw new PluginException($"{location} is larger than {MaxBytes / (1024 * 1024)} MB.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new PluginException($"Downloading {location} failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginException($"Downloading {location} timed out.", ex);
        }
    }

    /// <summary><paramref name="reference"/> relative to <paramref name="baseLocation"/>, unless it is absolute already.</summary>
    public static string Combine(string baseLocation, string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http" or "file")
        {
            return reference;
        }

        if (Path.IsPathFullyQualified(reference))
        {
            return reference;
        }

        if (LocalPath(baseLocation) is { } local)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(local))!, reference));
        }

        return new Uri(new Uri(baseLocation), reference).ToString();
    }

    /// <summary>The local path a location names, or null for a web URL.</summary>
    internal static string? LocalPath(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            // "C:\x" parses as a URI with scheme "c" on Windows.
            if (uri.Scheme is "https" or "http")
            {
                return null;
            }
        }

        return Path.GetFullPath(location);
    }
}

/// <summary>Downloads a plugin index and checks its signature before reading it.</summary>
public sealed class PluginIndexClient(PluginFetcher? fetcher = null, BlobSignatureVerifier? verifier = null, TimeProvider? clock = null)
{
    private readonly PluginFetcher _fetcher = fetcher ?? new PluginFetcher();
    private readonly BlobSignatureVerifier _verifier = verifier ?? new BlobSignatureVerifier();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The index at <paramref name="source"/>. Throws <see cref="PluginException"/> when it can't be
    /// downloaded, its signature isn't from the source's signer, or it has expired.
    /// </summary>
    public async Task<PluginIndex> LoadAsync(PluginIndexSource source, CancellationToken ct = default)
    {
        await _verifier.EnsureAvailableAsync("Reading the plugin index", ct);
        var work = Directory.CreateTempSubdirectory("deployify-index-").FullName;
        try
        {
            var file = Path.Combine(work, "index.json");
            var bundle = file + PluginIndex.BundleSuffix;
            await _fetcher.DownloadAsync(source.Url, file, ct);
            try
            {
                await _fetcher.DownloadAsync(source.Url + PluginIndex.BundleSuffix, bundle, ct);
            }
            catch (PluginException ex)
            {
                throw new PluginException($"The plugin index {source.Url} has no signature ({ex.Message}). Deployify doesn't read unsigned indexes.", ex);
            }

            if (await _verifier.VerifyAsync(file, bundle, source.Signer, ct) is { } problem)
            {
                throw new PluginException(
                    $"The plugin index {source.Url} isn't signed by {source.Signer.Describe()}: {problem}. It was not read.");
            }

            // Read exactly the bytes that were verified.
            var index = PluginIndex.Parse(await File.ReadAllBytesAsync(file, ct), source.Url);
            if (index.Expires is { } expires && expires < _clock.GetUtcNow())
            {
                throw new PluginException(
                    $"The plugin index {source.Url} expired on {expires:yyyy-MM-dd}. An expired index may hide withdrawn " +
                    "plugin versions, so it isn't used; its publisher has to rebuild it.");
            }

            return index;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
