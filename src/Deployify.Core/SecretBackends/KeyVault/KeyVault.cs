using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.SecretBackends.KeyVault;

/// <summary>A Key Vault secret as read back. The value stays wrapped.</summary>
public sealed record KeyVaultSecret(string Name, SensitiveValue Value, string Version);

/// <summary>A Key Vault secret's name and tags, as listed (lists never include values).</summary>
public sealed record KeyVaultSecretInfo(string Name, IReadOnlyDictionary<string, string> Tags);

/// <summary>The Key Vault operations the secret backend needs. Implemented by <see cref="AzureCliKeyVaultClient"/>.</summary>
public interface IKeyVaultClient
{
    /// <summary>Reads the current version of a secret, or null when it doesn't exist.</summary>
    Task<KeyVaultSecret?> GetSecretAsync(string vaultName, string name, CancellationToken ct = default);

    /// <summary>Lists the vault's secrets: names and tags only.</summary>
    Task<IReadOnlyList<KeyVaultSecretInfo>> ListSecretsAsync(string vaultName, CancellationToken ct = default);

    /// <summary>Reads one specific version of a secret, or null when it doesn't exist.</summary>
    Task<KeyVaultSecret?> GetSecretVersionAsync(string vaultName, string name, string version, CancellationToken ct = default)
        => throw new NotSupportedException("This Key Vault client can't read secret versions.");

    /// <summary>Writes a new version of a secret. Returns the version id.</summary>
    Task<string> SetSecretAsync(
        string vaultName, string name, string value, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default);
}

public static class KeyVaultSecrets
{
    /// <summary>Tag holding the prefix, so a list can find one environment's secrets in a shared vault.</summary>
    public const string StoreTag = "deployify-store";

    /// <summary>Tag holding the original key of a generated value (<c>pg-password.value</c>).</summary>
    public const string KeyTag = "deployify-key";

    /// <summary>Tag naming the app a secret belongs to.</summary>
    public const string AppTag = "deployify-app";

    private const int MaxNameLength = 127;

    /// <summary>
    /// A valid Key Vault secret name: letters, digits and <c>-</c>, at most 127 characters.
    /// Anything else becomes <c>-</c>. Names that would be too long are shortened and given a
    /// hash of the full name, so they stay unique.
    /// </summary>
    public static string SecretName(params string[] parts)
    {
        var raw = string.Join('-', parts);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            var next = char.IsAsciiLetterOrDigit(c) ? c : '-';
            if (next == '-' && sb.Length > 0 && sb[^1] == '-')
            {
                continue;
            }

            sb.Append(next);
        }

        var name = sb.ToString().Trim('-');
        if (name.Length == 0)
        {
            name = "secret";
        }

        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8].ToLowerInvariant();
        return $"{name[..(MaxNameLength - 9)].TrimEnd('-')}-{hash}";
    }

    /// <summary>Version-less secret URL: Container Apps then follows the latest version.</summary>
    public static string SecretUrl(KeyVaultConfig keyVault, string secretName)
        => $"{keyVault.ResolveVaultUri()}/secrets/{secretName}";

    /// <summary>The vault name for CLI calls.</summary>
    public static string VaultName(KeyVaultConfig keyVault)
        => !string.IsNullOrWhiteSpace(keyVault.VaultName)
            ? keyVault.VaultName
            : new Uri(keyVault.ResolveVaultUri()).Host.Split('.')[0];

    /// <summary>
    /// Writes a secret unless its current version already holds <paramref name="value"/>.
    /// Every write creates a version, and a changed version restarts nothing by itself, so
    /// skipping unchanged values keeps both the history and restarts meaningful.
    /// </summary>
    public static async Task<(string Version, bool Written, bool Existed)> SetIfChangedAsync(
        IKeyVaultClient client, string vaultName, string name, SensitiveValue value,
        IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
    {
        var current = await client.GetSecretAsync(vaultName, name, ct);
        if (current is not null && current.Value.Equals(value))
        {
            return (current.Version, false, true);
        }

        return (await client.SetSecretAsync(vaultName, name, value.Reveal(), tags, ct), true, current is not null);
    }
}

/// <summary>
/// Keeps generated parameter values as Key Vault secrets named <c>{prefix}-parameters-{key}</c>,
/// tagged with the store prefix and the original key (Key Vault names can't hold <c>.</c>).
/// </summary>
public sealed class KeyVaultSecretStore(IKeyVaultClient client, string vaultName, string prefix) : ISecretStore
{
    public string Description => $"Key Vault {vaultName} ({prefix}-parameters-*)";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, SensitiveValue>(StringComparer.Ordinal);
        var mine = (await client.ListSecretsAsync(vaultName, ct)).Where(s =>
            s.Tags.TryGetValue(KeyVaultSecrets.StoreTag, out var store) && store == prefix &&
            s.Tags.ContainsKey(KeyVaultSecrets.KeyTag));
        foreach (var info in mine)
        {
            if (await client.GetSecretAsync(vaultName, info.Name, ct) is { } secret)
            {
                result[info.Tags[KeyVaultSecrets.KeyTag]] = secret.Value;
            }
        }

        return result;
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        foreach (var key in changed)
        {
            await client.SetSecretAsync(vaultName, KeyVaultSecrets.SecretName(prefix, "parameters", key), all[key].Reveal(),
                new Dictionary<string, string> { [KeyVaultSecrets.StoreTag] = prefix, [KeyVaultSecrets.KeyTag] = key }, ct);
        }
    }
}
