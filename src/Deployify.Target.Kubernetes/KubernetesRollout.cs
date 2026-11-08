using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deployify.Core.Interfaces;

namespace Deployify.Target.Kubernetes;

/// <summary>The parts of a Deployment (<c>kubectl get deployment -o json</c>) the rollout needs.</summary>
public sealed record KubernetesDeploymentStatus(
    long Generation,
    long ObservedGeneration,
    long? RolloutRevision,
    int SpecReplicas,
    int Replicas,
    int UpdatedReplicas,
    int AvailableReplicas,
    string? ProgressDeadlineMessage)
{
    public static KubernetesDeploymentStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var metadata = Obj(root, "metadata");
        var spec = Obj(root, "spec");
        var status = Obj(root, "status");

        long? revision = null;
        if (metadata is { } m && Obj(m, "annotations") is { } annotations &&
            annotations.TryGetProperty("deployment.kubernetes.io/revision", out var rev) &&
            long.TryParse(rev.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
        {
            revision = r;
        }

        string? deadline = null;
        if (status is { } st && st.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conditions.EnumerateArray())
            {
                if (Str(c, "type") == "Progressing" && Str(c, "reason") == "ProgressDeadlineExceeded")
                {
                    deadline = Str(c, "message") ?? "the rollout exceeded its progress deadline";
                }
            }
        }

        return new KubernetesDeploymentStatus(
            metadata is { } md ? Long(md, "generation") : 0,
            status is { } s1 ? Long(s1, "observedGeneration") : 0,
            revision,
            spec is { } sp && sp.TryGetProperty("replicas", out var rep) && rep.ValueKind == JsonValueKind.Number ? rep.GetInt32() : 1,
            status is { } s2 ? (int)Long(s2, "replicas") : 0,
            status is { } s3 ? (int)Long(s3, "updatedReplicas") : 0,
            status is { } s4 ? (int)Long(s4, "availableReplicas") : 0,
            deadline);
    }

    private static JsonElement? Obj(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}

/// <summary>
/// Turns a Deployment's status into a health observation, the way <c>kubectl rollout status</c>
/// judges it. A revision here is the Deployment's <c>metadata.generation</c>: the API server bumps it
/// as soon as the spec changes, whereas the rollout revision annotation is only set later by the
/// controller and would let the gate pass on the old pods.
/// </summary>
internal static class KubernetesRolloutHealth
{
    public static HealthObservation Evaluate(KubernetesDeploymentStatus? status, long generation)
    {
        if (status is null)
        {
            return HealthObservation.Failed("the deployment does not exist");
        }

        if (status.Generation > generation)
        {
            return HealthObservation.Failed($"the deployment was changed again (generation {status.Generation})");
        }

        if (status.ObservedGeneration < generation)
        {
            return HealthObservation.Progressing("waiting for the deployment controller");
        }

        if (status.ProgressDeadlineMessage is { } deadline)
        {
            return HealthObservation.Failed(deadline);
        }

        var detail = $"{status.AvailableReplicas}/{status.SpecReplicas} pod(s) available";
        if (status.UpdatedReplicas < status.SpecReplicas)
        {
            return HealthObservation.Progressing($"{status.UpdatedReplicas}/{status.SpecReplicas} pod(s) updated");
        }

        if (status.Replicas > status.UpdatedReplicas)
        {
            return HealthObservation.Progressing($"{detail}, {status.Replicas - status.UpdatedReplicas} old pod(s) terminating");
        }

        return status.AvailableReplicas >= status.SpecReplicas
            ? HealthObservation.Healthy(detail)
            : HealthObservation.Progressing(detail);
    }

    /// <summary>A ConfigMap object for server-side apply, holding only <paramref name="data"/>.</summary>
    public static string ConfigMapJson(
        string namespaceName, string name, IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var dataNode = new JsonObject();
        foreach (var (k, v) in data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            dataNode[k] = v;
        }

        var metadata = new JsonObject { ["name"] = name, ["namespace"] = namespaceName };
        if (labels is not null)
        {
            var labelNode = new JsonObject();
            foreach (var (k, v) in labels)
            {
                labelNode[k] = v;
            }

            metadata["labels"] = labelNode;
        }

        return new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "ConfigMap",
            ["metadata"] = metadata,
            ["data"] = dataNode
        }.ToJsonString();
    }
}
