using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.SecretBackends.Vault;

/// <summary>Keeps generated parameter values in one Vault KV v2 entry.</summary>
public sealed class VaultSecretStore(Func<VaultClient> clientFactory, string mount, string path) : ISecretStore
{
    private int _version;

    public string Description => $"Vault {VaultClient.KvApiPath(mount, path)}";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
    {
        using var client = clientFactory();
        var entry = await client.ReadKvAsync(mount, path, ct);
        _version = entry.Version;
        return entry.Data;
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        using var client = clientFactory();
        // Check-and-set against the version read in LoadAsync: if another deploy stored values
        // in between, fail rather than replace them.
        _version = await client.WriteKvAsync(mount, path, all, _version, ct);
    }
}
