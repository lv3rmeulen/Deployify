using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.SecretBackends;

namespace Deployify.Target.AwsEcs;

/// <summary>An SSM parameter as read back. The value stays wrapped.</summary>
public sealed record SsmParameter(string Name, SensitiveValue Value, long Version);

/// <summary>The SSM Parameter Store operations the secret backend needs. Implemented by <see cref="EcsCliClient"/>.</summary>
public interface ISsmParameterClient
{
    /// <summary>Reads a SecureString parameter, decrypted, or null when it doesn't exist.</summary>
    Task<SsmParameter?> GetParameterAsync(string name, CancellationToken ct = default);

    /// <summary>Reads every parameter under <paramref name="path"/>, decrypted, keyed by full name.</summary>
    Task<IReadOnlyDictionary<string, SsmParameter>> GetParametersByPathAsync(string path, CancellationToken ct = default);

    /// <summary>Creates or overwrites a SecureString parameter. Returns its new version.</summary>
    Task<long> PutSecureStringParameterAsync(string name, string value, CancellationToken ct = default);
}

public static class SsmSecrets
{
    /// <summary>
    /// Writes a SecureString unless it already holds <paramref name="value"/>. Every put creates a
    /// new parameter version, so skipping unchanged values keeps the history meaningful.
    /// </summary>
    public static async Task<(long Version, bool Written)> PutIfChangedAsync(
        ISsmParameterClient client, string name, SensitiveValue value, CancellationToken ct = default)
    {
        var current = await client.GetParameterAsync(name, ct);
        if (current is not null && current.Value.Equals(value))
        {
            return (current.Version, false);
        }

        return (await client.PutSecureStringParameterAsync(name, value.Reveal(), ct), true);
    }
}

/// <summary>Keeps generated parameter values as SecureStrings under one path: <c>{prefix}/_parameters/{key}</c>.</summary>
public sealed class SsmSecretStore(ISsmParameterClient client, string path) : ISecretStore
{
    private readonly string _path = path.TrimEnd('/');

    public string Description => $"SSM Parameter Store {_path}";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
    {
        var prefix = _path + "/";
        return (await client.GetParametersByPathAsync(_path, ct))
            .Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key[prefix.Length..], p => p.Value.Value, StringComparer.Ordinal);
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        // One parameter per value: only new values need writing.
        foreach (var key in changed)
        {
            await client.PutSecureStringParameterAsync($"{_path}/{SecretKeys.Sanitize(key)}", all[key].Reveal(), ct);
        }
    }
}
