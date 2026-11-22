using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.SecretBackends;

namespace Deployify.Target.CloudRun;

/// <summary>
/// Generated parameter values (passwords from <c>default.generate</c>) kept in Google Secret
/// Manager as one secret, <c>{prefix}-generated</c>, holding a JSON object. Each save adds a version
/// with the complete set.
/// </summary>
public sealed class GcpSecretManagerStore(GcloudClient client, string project, string prefix) : ISecretStore
{
    public string SecretName => $"{prefix}-generated";

    public string Description => $"Secret Manager secret {SecretName} in project {project}";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
    {
        var json = await client.AccessLatestAsync(project, SecretName, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, SensitiveValue>();
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return values.ToDictionary(kv => kv.Key, kv => new SensitiveValue(kv.Value), StringComparer.Ordinal);
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(all.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value.Reveal()));
        await client.WriteAsync(project, SecretName, json, new Dictionary<string, string>
        {
            [CloudRunManifestGenerator.ManagedByLabel] = CloudRunManifestGenerator.ManagedByValue,
            ["deployify-store"] = CloudRunManifestGenerator.LabelValue(prefix)
        }, ct);
    }
}
