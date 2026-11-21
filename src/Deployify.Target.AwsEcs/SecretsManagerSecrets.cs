using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.SecretBackends;

namespace Deployify.Target.AwsEcs;

/// <summary>A Secrets Manager secret's current (<c>AWSCURRENT</c>) version as read back. The value stays wrapped.</summary>
public sealed record SecretsManagerSecret(string Arn, string Name, SensitiveValue Value, string VersionId);

/// <summary>Where a secret version was written: the secret's full ARN and the new version's ID.</summary>
public sealed record SecretsManagerVersion(string Arn, string VersionId);

/// <summary>The Secrets Manager operations the secret backend needs. Implemented by <see cref="EcsCliClient"/>.</summary>
public interface ISecretsManagerClient
{
    /// <summary>Reads a secret's current value, or null when it doesn't exist or is scheduled for deletion.</summary>
    Task<SecretsManagerSecret?> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Stores a new current version of a secret, creating it (tagged as Deployify's) when it doesn't
    /// exist and restoring it first when it is scheduled for deletion.
    /// </summary>
    Task<SecretsManagerVersion> PutSecretAsync(string name, string value, CancellationToken ct = default);

    /// <summary>Schedules a secret for deletion. Returns false when there was no such secret.</summary>
    Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default);
}

public static class SecretsManagerSecrets
{
    /// <summary>
    /// The JSON document a service's secret holds: one string property per key, sorted, so the
    /// same values always give the same document.
    /// </summary>
    public static string Document(IEnumerable<KeyValuePair<string, SensitiveValue>> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[key] = value.Reveal();
        }

        return obj.ToJsonString();
    }

    /// <summary>Reads a document written by <see cref="Document"/>. Non-string properties are skipped.</summary>
    public static IReadOnlyDictionary<string, SensitiveValue> ParseDocument(SensitiveValue document)
    {
        var result = new Dictionary<string, SensitiveValue>(StringComparer.Ordinal);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(document.Reveal());
        }
        catch (JsonException)
        {
            // Never put the parser's message in the error: it can quote the value.
            throw new InvalidOperationException("The secret doesn't hold a JSON object of string values.");
        }

        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException("The secret doesn't hold a JSON object of string values.");
        }

        foreach (var (key, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                result[key] = new SensitiveValue(s);
            }
        }

        return result;
    }

    /// <summary>
    /// Writes <paramref name="values"/> as the secret's new version unless it already holds exactly
    /// them. Every put creates a version, so skipping unchanged documents keeps old task definition
    /// revisions (which are pinned to a version) and the version history meaningful.
    /// </summary>
    public static async Task<(SecretsManagerVersion Version, bool Written)> PutIfChangedAsync(
        ISecretsManagerClient client, string name, IReadOnlyDictionary<string, SensitiveValue> values,
        CancellationToken ct = default)
    {
        var current = await client.GetSecretAsync(name, ct);
        if (current is not null && SameValues(TryParse(current.Value), values))
        {
            return (new SecretsManagerVersion(current.Arn, current.VersionId), false);
        }

        return (await client.PutSecretAsync(name, Document(values), ct), true);
    }

    private static IReadOnlyDictionary<string, SensitiveValue>? TryParse(SensitiveValue document)
    {
        try
        {
            return ParseDocument(document);
        }
        catch (InvalidOperationException)
        {
            return null; // not ours or hand-edited: overwrite it
        }
    }

    private static bool SameValues(
        IReadOnlyDictionary<string, SensitiveValue>? a, IReadOnlyDictionary<string, SensitiveValue> b)
        => a is not null && a.Count == b.Count &&
           a.All(kv => b.TryGetValue(kv.Key, out var other) && other.Equals(kv.Value));
}

/// <summary>
/// Keeps generated parameter values as one JSON secret, <c>{prefix}/_parameters</c>. The whole
/// document is written each time, so a save writes every value, not only the changed ones.
/// </summary>
public sealed class SecretsManagerSecretStore(
    ISecretsManagerClient client, string name, ISsmParameterClient? ssm = null, string? ssmPath = null) : ISecretStore
{
    private bool _exists;

    public string Description => $"AWS Secrets Manager secret {name}";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
    {
        if (await client.GetSecretAsync(name, ct) is { } secret)
        {
            _exists = true;
            return SecretsManagerSecrets.ParseDocument(secret.Value);
        }

        return new Dictionary<string, SensitiveValue>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Until this store's secret exists: the values the Ssm backend generated, so switching
    /// backends doesn't change a database password. The next apply writes them here.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadCarriedOverAsync(CancellationToken ct = default)
        => _exists || ssm is null || ssmPath is null
            ? new Dictionary<string, SensitiveValue>(StringComparer.Ordinal)
            : await new SsmSecretStore(ssm, ssmPath).LoadAsync(ct);

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
        => await SecretsManagerSecrets.PutIfChangedAsync(client, name, all, ct);
}
