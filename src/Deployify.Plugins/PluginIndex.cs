using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deployify.Core.Models;
using Deployify.Core.Versioning;
using Deployify.Plugin.Contracts;

namespace Deployify.Plugins;

/// <summary>
/// A plugin index: the third-party target plugins Deployify can find and install by ID, who
/// publishes each one, and the signed packages of each version.
/// </summary>
/// <remarks>
/// <para>
/// Trust runs in a chain. The index itself is signed (<c>index.json.sigstore.json</c> next to it),
/// and Deployify only reads an index whose signature matches the signer it trusts for that index.
/// Each plugin entry names the <see cref="Signer"/> its publisher signs packages with, so the
/// index vouches for "plugin <c>example</c> is published by whoever controls this identity". Each
/// package is pinned by SHA-256 and carries its own Sigstore bundle, which must be signed by that
/// publisher. After installing, <c>deployify-plugins.lock.json</c> pins the installed files.
/// </para>
/// <para>Format version 1. New optional fields can be added within version 1.</para>
/// </remarks>
public sealed class PluginIndex
{
    /// <summary>The format version this Deployify reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The suffix of the Sigstore bundle that signs a file: <c>index.json.sigstore.json</c>.</summary>
    public const string BundleSuffix = ".sigstore.json";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Format version of the file.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>When the index was built.</summary>
    public DateTimeOffset? Generated { get; set; }

    /// <summary>
    /// After this the index is refused, so an old signed copy can't be served forever to hide a
    /// yanked version. The published index is rebuilt and re-signed weekly.
    /// </summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>The plugins, by target ID.</summary>
    public SortedDictionary<string, PluginIndexEntry> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads an index, checking that it is a version this Deployify understands and that its entries are sound.</summary>
    public static PluginIndex Parse(ReadOnlySpan<byte> json, string source)
    {
        PluginIndex index;
        try
        {
            index = JsonSerializer.Deserialize<PluginIndex>(json, Json) ?? throw new JsonException("The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new PluginException($"The plugin index {source} can't be read: {ex.Message}", ex);
        }

        if (index.Version != CurrentVersion)
        {
            throw new PluginException(
                $"The plugin index {source} is format version {index.Version}; this Deployify reads version {CurrentVersion}. Update Deployify.");
        }

        index.Plugins = new SortedDictionary<string, PluginIndexEntry>(index.Plugins, StringComparer.OrdinalIgnoreCase);
        var problems = index.Plugins.SelectMany(p => p.Value.Validate(p.Key, requireHashes: true).Select(e => $"{p.Key}: {e}")).ToList();
        if (problems.Count > 0)
        {
            throw new PluginException($"The plugin index {source} is invalid:{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", problems)}");
        }

        return index;
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json) + "\n";

    public PluginIndexEntry? Find(string targetId) => Plugins.GetValueOrDefault(targetId);

    /// <summary>
    /// Plugins whose ID, name, publisher or description contains <paramref name="term"/>
    /// (all of them when it is empty), IDs that match exactly first.
    /// </summary>
    public IEnumerable<KeyValuePair<string, PluginIndexEntry>> Search(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Plugins;
        }

        term = term.Trim();
        return Plugins
            .Where(p => Contains(p.Key) || Contains(p.Value.Name) || Contains(p.Value.Publisher) || Contains(p.Value.Description))
            .OrderBy(p => string.Equals(p.Key, term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase);

        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>One plugin in the index.</summary>
public sealed class PluginIndexEntry
{
    /// <summary>Display name, such as "DigitalOcean App Platform".</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Who publishes it: a person or organization.</summary>
    public string? Publisher { get; set; }

    public string? Homepage { get; set; }

    /// <summary>SPDX license expression.</summary>
    public string? License { get; set; }

    /// <summary>Who must have signed every package of this plugin.</summary>
    public PluginSigner? Signer { get; set; }

    /// <summary>Released versions, any order. The newest that isn't yanked is installed by default.</summary>
    public List<PluginIndexVersion> Versions { get; set; } = [];

    /// <summary>The newest version that isn't yanked, or null when there is none.</summary>
    public PluginIndexVersion? Latest(bool includePrerelease = false)
        => Versions
            .Where(v => v.Yanked is null && (includePrerelease || !v.IsPrerelease))
            .OrderByDescending(v => v.Version, VersionOrder.Comparer)
            .FirstOrDefault()
           ?? (includePrerelease ? null : Latest(includePrerelease: true));

    /// <summary>
    /// The version <c>deployify plugin update</c> moves <paramref name="current"/> to: the newest
    /// one that isn't yanked, speaks this Deployify's plugin protocol and is newer than
    /// <paramref name="current"/>. Unless <paramref name="crossMajor"/>, only versions without
    /// breaking changes count (<see cref="VersionOrder.IsCompatible"/>). Pre-releases count when
    /// <paramref name="includePrerelease"/> or when <paramref name="current"/> is one. Null when
    /// there is nothing newer.
    /// </summary>
    public PluginIndexVersion? UpdateFor(string current, bool includePrerelease = false, bool crossMajor = false)
        => Versions
            .Where(v => v.Version is not null && v.Yanked is null && v.SpeaksCurrentProtocol)
            .Where(v => VersionOrder.IsUpdate(current, v.Version!, includePrerelease, crossMajor))
            .OrderByDescending(v => v.Version, VersionOrder.Comparer)
            .FirstOrDefault();

    public PluginIndexVersion? FindVersion(string version)
        => Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>What is wrong with this entry, empty when nothing is.</summary>
    /// <param name="requireHashes">A published index pins every package; index sources may leave the hash to the build.</param>
    public IEnumerable<string> Validate(string targetId, bool requireHashes)
    {
        if (!DeploymentTargetType.TryParse(targetId, out var target))
        {
            yield return $"'{targetId}' isn't a valid target ID (lowercase letters, digits and '-').";
        }
        else if (target.IsBuiltIn)
        {
            yield return $"'{targetId}' is a built-in target; a plugin can't take it over.";
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "'name' is required.";
        }

        if (string.IsNullOrWhiteSpace(Publisher))
        {
            yield return "'publisher' is required.";
        }

        if (Signer is null)
        {
            yield return "'signer' is required: who signs this plugin's packages.";
        }
        else
        {
            foreach (var problem in Signer.Validate())
            {
                yield return $"signer: {problem}";
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in Versions)
        {
            if (!seen.Add(version.Version ?? string.Empty))
            {
                yield return $"version {version.Version} is listed twice.";
            }

            foreach (var problem in version.Validate(requireHashes))
            {
                yield return $"{version.Version ?? "(no version)"}: {problem}";
            }
        }
    }
}

/// <summary>One released version of a plugin.</summary>
public sealed class PluginIndexVersion
{
    /// <summary>SemVer, exactly as the plugin reports it in its handshake.</summary>
    public string? Version { get; set; }

    /// <summary>The plugin protocol version it speaks, such as <c>1.0</c>.</summary>
    public string? Protocol { get; set; }

    /// <summary>Why this version was withdrawn. A yanked version is only installed when asked for by version (or by a lock file), with a warning.</summary>
    public string? Yanked { get; set; }

    /// <summary>
    /// Packages by platform: a runtime ID such as <c>linux-x64</c>, or <see cref="PluginFiles.AnyPlatform"/>
    /// for a framework-dependent plugin that runs anywhere .NET does.
    /// </summary>
    public SortedDictionary<string, PluginPackage> Packages { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsPrerelease => Version?.Contains('-', StringComparison.Ordinal) == true;

    /// <summary>The package for <paramref name="platform"/>: its own, else the one for any platform.</summary>
    public (string Platform, PluginPackage Package)? PackageFor(string platform)
        => Packages.TryGetValue(platform, out var own) ? (platform, own)
            : Packages.TryGetValue(PluginFiles.AnyPlatform, out var any) ? (PluginFiles.AnyPlatform, any)
            : null;

    /// <summary>Whether this Deployify can talk to this version: no protocol listed, or the same major protocol version.</summary>
    [JsonIgnore]
    public bool SpeaksCurrentProtocol
        => Protocol is not { } protocol || !System.Version.TryParse(protocol, out var speaks) || speaks.Major == PluginProtocol.Version.Major;

    internal IEnumerable<string> Validate(bool requireHashes)
    {
        if (string.IsNullOrWhiteSpace(Version) || !System.Text.RegularExpressions.Regex.IsMatch(
                Version, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$"))
        {
            yield return "'version' must be a SemVer version such as 1.2.0.";
        }

        if (Protocol is not null && !System.Version.TryParse(Protocol, out _))
        {
            yield return $"'protocol' '{Protocol}' isn't a version such as {PluginProtocol.Version.ToString(2)}.";
        }

        if (Packages.Count == 0)
        {
            yield return "no packages.";
        }

        foreach (var (platform, package) in Packages)
        {
            if (string.IsNullOrWhiteSpace(platform) || platform.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))
            {
                yield return $"'{platform}' isn't a runtime ID such as linux-x64, or '{PluginFiles.AnyPlatform}'.";
            }

            if (string.IsNullOrWhiteSpace(package.Url))
            {
                yield return $"{platform}: 'url' is required.";
            }

            if (requireHashes || package.Sha256 is not null)
            {
                if (package.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
                {
                    yield return $"{platform}: 'sha256' must be the 64-character hex SHA-256 of the package.";
                }
            }
        }
    }
}

/// <summary>A plugin package: a <c>.zip</c> of the plugin's folder, signed by its publisher.</summary>
public sealed class PluginPackage
{
    /// <summary>Where to download it. Relative URLs are relative to the index.</summary>
    public string? Url { get; set; }

    /// <summary>SHA-256 of the <c>.zip</c>, lowercase hex.</summary>
    public string? Sha256 { get; set; }

    /// <summary>The Sigstore bundle (<c>cosign sign-blob --bundle</c>). Defaults to <see cref="Url"/> + <c>.sigstore.json</c>.</summary>
    public string? Bundle { get; set; }

    [JsonIgnore]
    public string BundleUrl => string.IsNullOrWhiteSpace(Bundle) ? Url + PluginIndex.BundleSuffix : Bundle;
}

/// <summary>
/// Who must have signed something, in cosign's terms: a keyless Sigstore identity (the
/// certificate identity and the OIDC issuer that vouched for it), or a public key.
/// </summary>
public sealed record PluginSigner
{
    /// <summary>The exact certificate identity, such as <c>https://github.com/acme/deployify-target-example/.github/workflows/release.yml@refs/tags/v1.0.0</c>.</summary>
    public string? Identity { get; init; }

    /// <summary>
    /// A regular expression the identity must match, anchored with <c>^…$</c>, such as
    /// <c>^https://github\.com/acme/deployify-target-example/\.github/workflows/release\.yml@refs/tags/v.*$</c>.
    /// </summary>
    public string? IdentityRegexp { get; init; }

    /// <summary>The OIDC issuer, such as <c>https://token.actions.githubusercontent.com</c>.</summary>
    public string? Issuer { get; init; }

    /// <summary>A PEM public key (<c>cosign.pub</c>), for packages signed with a key instead of keyless.</summary>
    public string? PublicKey { get; init; }

    /// <summary>GitHub Actions' OIDC issuer.</summary>
    public const string GitHubActionsIssuer = "https://token.actions.githubusercontent.com";

    public IEnumerable<string> Validate()
    {
        var hasKey = !string.IsNullOrWhiteSpace(PublicKey);
        var hasIdentity = !string.IsNullOrWhiteSpace(Identity) || !string.IsNullOrWhiteSpace(IdentityRegexp);
        if (hasKey && (hasIdentity || !string.IsNullOrWhiteSpace(Issuer)))
        {
            yield return "set either 'publicKey' or a keyless 'identity'/'identityRegexp' and 'issuer', not both.";
            yield break;
        }

        if (hasKey)
        {
            if (!PublicKey!.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            {
                yield return "'publicKey' must be a PEM public key (the contents of cosign.pub).";
            }

            yield break;
        }

        if (!hasIdentity || string.IsNullOrWhiteSpace(Issuer))
        {
            // Without both, any Sigstore signature would pass.
            yield return "needs 'identity' (or 'identityRegexp') and 'issuer', or 'publicKey'.";
        }

        if (!string.IsNullOrWhiteSpace(Identity) && !string.IsNullOrWhiteSpace(IdentityRegexp))
        {
            yield return "set 'identity' or 'identityRegexp', not both.";
        }

        if (IdentityRegexp is { Length: > 0 } pattern)
        {
            if (!pattern.StartsWith('^') || !pattern.EndsWith('$'))
            {
                // Unanchored, "github.com/acme/" also matches github.com/acme-evil/….
                yield return "'identityRegexp' must be anchored: start with ^ and end with $.";
            }

            string? error = null;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
            }

            if (error is not null)
            {
                yield return $"'identityRegexp' isn't a valid regular expression: {error}";
            }
        }
    }

    /// <summary>For messages: "keyless, &lt;identity&gt;, issued by &lt;issuer&gt;" or "public key".</summary>
    public string Describe()
        => !string.IsNullOrWhiteSpace(PublicKey)
            ? "public key " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(PublicKey.Trim())))[..16]
            : $"keyless, {(string.IsNullOrWhiteSpace(Identity) ? $"/{IdentityRegexp}/" : Identity)}, issued by {Issuer}";
}
