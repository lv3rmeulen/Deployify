using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.SecretBackends;

namespace Deployify.Target.Kubernetes;

/// <summary>The cluster operations the secret backends need. Implemented by <see cref="KubectlRunner"/>.</summary>
public interface IKubernetesSecretClient
{
    /// <summary>Reads a Secret's data, or null when it doesn't exist. Output is never logged.</summary>
    Task<IReadOnlyDictionary<string, SensitiveValue>?> GetSecretDataAsync(
        string namespaceName, string name, CancellationToken ct = default);

    /// <summary>Returns an object's resourceVersion, or null when it doesn't exist.</summary>
    Task<string?> GetResourceVersionAsync(string kind, string namespaceName, string name, CancellationToken ct = default);

    /// <summary>Server-side applies one JSON object from memory and returns its new resourceVersion.</summary>
    Task<string> ServerSideApplyAsync(string json, CancellationToken ct = default);
}

/// <summary>Builds and reads Secret objects. The JSON is only ever held in memory and sent on stdin.</summary>
public static class KubernetesSecretManifest
{
    public static string Build(
        string namespaceName, string name, IReadOnlyDictionary<string, SensitiveValue> data,
        IReadOnlyDictionary<string, string>? labels = null, string type = "Opaque")
    {
        var labelNode = new JsonObject { ["app.kubernetes.io/managed-by"] = "deployify" };
        foreach (var (k, v) in labels ?? new Dictionary<string, string>())
        {
            labelNode[k] = v;
        }

        var dataNode = new JsonObject();
        foreach (var (key, value) in data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            dataNode[key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Reveal()));
        }

        return new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = namespaceName, ["labels"] = labelNode },
            ["type"] = type,
            ["data"] = dataNode
        }.ToJsonString();
    }

    /// <summary>
    /// A <c>kubernetes.io/dockerconfigjson</c> Secret holding one registry login, as
    /// <c>kubectl create secret docker-registry</c> makes it.
    /// </summary>
    public static string DockerRegistry(
        string namespaceName, string name, string server, string username, SensitiveValue password,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password.Reveal()}"));
        var config = new JsonObject
        {
            ["auths"] = new JsonObject
            {
                [server] = new JsonObject { ["username"] = username, ["password"] = password.Reveal(), ["auth"] = auth }
            }
        }.ToJsonString();
        return Build(namespaceName, name, new Dictionary<string, SensitiveValue> { [".dockerconfigjson"] = new(config) },
            labels, "kubernetes.io/dockerconfigjson");
    }

    /// <summary>
    /// A Namespace object. By default it gets no managed-by label, so plan doesn't list the
    /// secrets namespace as a service to delete; pass the service labels for a service namespace.
    /// </summary>
    public static string Namespace(string name, IReadOnlyDictionary<string, string>? labels = null)
    {
        var labelNode = new JsonObject();
        foreach (var (k, v) in labels ?? new Dictionary<string, string> { ["deployify/role"] = "secrets" })
        {
            labelNode[k] = v;
        }

        return new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Namespace",
            ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = labelNode }
        }.ToJsonString();
    }

    /// <summary>Decodes the <c>data</c> of a Secret returned by <c>kubectl get secret -o json</c>.</summary>
    public static IReadOnlyDictionary<string, SensitiveValue> ReadData(string secretJson)
    {
        using var doc = JsonDocument.Parse(secretJson);
        var result = new Dictionary<string, SensitiveValue>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in data.EnumerateObject())
            {
                result[prop.Name] = new SensitiveValue(Encoding.UTF8.GetString(Convert.FromBase64String(prop.Value.GetString() ?? "")));
            }
        }

        return result;
    }
}

/// <summary>One entry of a container's <c>env</c> list: a <c>secretKeyRef</c> or a value.</summary>
public sealed record KubernetesEnvVar(string Name, string? Value = null, string? SecretName = null, string? SecretKey = null)
{
    public bool IsSecretRef => SecretKey is not null;
}

/// <summary>
/// Turns a service's secret env vars into <c>env</c> entries that only reference a Secret.
/// A value that is exactly one secret is a plain <c>secretKeyRef</c>. A value that embeds a
/// secret, such as a connection string, uses Kubernetes dependent variables: the secret is
/// loaded into its own variable first and the value refers to it as <c>$(NAME)</c>, which the
/// kubelet expands when the container starts. The manifest holds no secret either way.
/// </summary>
public static class KubernetesSecretEnv
{
    public static IReadOnlyList<KubernetesEnvVar> Render(
        string secretName,
        IReadOnlyDictionary<string, ResolvedValue> secretEnv,
        IReadOnlyCollection<string>? plainNames = null)
    {
        var entries = new List<KubernetesEnvVar>();
        var provided = new Dictionary<string, string>(StringComparer.Ordinal); // secret id → env var holding it
        var taken = new HashSet<string>(secretEnv.Keys, StringComparer.Ordinal);
        taken.UnionWith(plainNames ?? []);

        var ordered = secretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var composed = new List<KeyValuePair<string, ResolvedValue>>();
        foreach (var kv in ordered)
        {
            if (SecretKeys.IsSingleSecret(kv.Value, out var p))
            {
                var id = SecretKeys.Id(p);
                entries.Add(new KubernetesEnvVar(kv.Key, SecretName: secretName, SecretKey: id));
                provided.TryAdd(id, kv.Key);
            }
            else
            {
                composed.Add(kv);
            }
        }

        // A variable can only refer to variables defined before it, so helpers come first.
        foreach (var p in SecretKeys.SecretParameters(composed.Select(c => c.Value)))
        {
            var id = SecretKeys.Id(p);
            if (provided.ContainsKey(id))
            {
                continue;
            }

            var name = ResolvedEnvironment.VariableName(p);
            while (!taken.Add(name))
            {
                name += "_";
            }

            entries.Add(new KubernetesEnvVar(name, SecretName: secretName, SecretKey: id));
            provided[id] = name;
        }

        foreach (var (key, value) in composed)
        {
            entries.Add(new KubernetesEnvVar(key, value.Render(
                parameter: p => p.IsSecret ? $"$({provided[SecretKeys.Id(p)]})" : Escape(p.Value),
                unresolved: u => Escape(u.Placeholder),
                text: Escape)));
        }

        return entries;
    }

    /// <summary>
    /// Escapes literal text for an env <c>value</c>. Kubernetes reads <c>$(NAME)</c> as a
    /// reference and <c>$$</c> as a literal <c>$</c>, so doubling every <c>$</c> keeps text as-is.
    /// </summary>
    public static string Escape(string text) => text.Replace("$", "$$", StringComparison.Ordinal);
}

/// <summary>
/// Keeps generated parameter values in one Secret, in a namespace of its own
/// (<c>{prefix}-system</c>), instead of deployify-parameters.json.
/// </summary>
public sealed class KubernetesSecretStore(IKubernetesSecretClient client, string namespaceName, string name = KubernetesSecretStore.DefaultName)
    : ISecretStore
{
    public const string DefaultName = "deployify-parameters";

    public string Description => $"Kubernetes secret {namespaceName}/{name}";

    public async Task<IReadOnlyDictionary<string, SensitiveValue>> LoadAsync(CancellationToken ct = default)
        => await client.GetSecretDataAsync(namespaceName, name, ct) ?? new Dictionary<string, SensitiveValue>();

    public async Task SaveAsync(
        IReadOnlyDictionary<string, SensitiveValue> all, IReadOnlyCollection<string> changed, CancellationToken ct = default)
    {
        await client.ServerSideApplyAsync(KubernetesSecretManifest.Namespace(namespaceName), ct);
        // Server-side apply removes keys the field manager no longer sends, so always send all of them.
        await client.ServerSideApplyAsync(KubernetesSecretManifest.Build(namespaceName, name, all), ct);
    }
}
