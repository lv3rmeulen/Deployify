using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;

namespace Deployify.Target.AzureContainerApps;

/// <summary>The parts of <c>az containerapp show</c> the rollout needs.</summary>
public sealed record AcaAppStatus(string? LatestRevision, string? LatestReadyRevision)
{
    /// <summary><c>Single</c> or <c>Multiple</c>; null when Azure doesn't say (Single).</summary>
    public string? ActiveRevisionsMode { get; init; }

    /// <summary>The ingress host name, e.g. <c>api.happy-sea-1234.westeurope.azurecontainerapps.io</c>.</summary>
    public string? Fqdn { get; init; }

    /// <summary>The ingress traffic split.</summary>
    public IReadOnlyList<AcaTrafficWeight> Traffic { get; init; } = [];

    public bool MultipleRevisions => string.Equals(ActiveRevisionsMode, "Multiple", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The percentage of the traffic <paramref name="revision"/> gets.</summary>
    public int WeightOf(string revision)
        => Traffic.Where(t => t.RevisionName == revision || (t.LatestRevision && LatestRevision == revision)).Sum(t => t.Weight);

    public static AcaAppStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;
        var configuration = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("configuration", out var c) ? c : default;
        var ingress = configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("ingress", out var i) ? i : default;
        var traffic = new List<AcaTrafficWeight>();
        if (ingress.ValueKind == JsonValueKind.Object && ingress.TryGetProperty("traffic", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in t.EnumerateArray())
            {
                traffic.Add(new AcaTrafficWeight(
                    AcaJson.Str(entry, "revisionName"),
                    entry.TryGetProperty("latestRevision", out var latest) && latest.ValueKind == JsonValueKind.True,
                    entry.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0,
                    AcaJson.Str(entry, "label")));
            }
        }

        return new AcaAppStatus(
            AcaJson.Str(props, "latestRevisionName"),
            AcaJson.Str(props, "latestReadyRevisionName"))
        {
            ActiveRevisionsMode = AcaJson.Str(configuration, "activeRevisionsMode"),
            Fqdn = AcaJson.Str(ingress, "fqdn"),
            Traffic = traffic
        };
    }
}

/// <summary>One entry of an app's ingress traffic split.</summary>
public sealed record AcaTrafficWeight(string? RevisionName, bool LatestRevision, int Weight, string? Label);

/// <summary>The parts of <c>az containerapp revision show</c> the rollout needs.</summary>
public sealed record AcaRevisionStatus(
    bool Active,
    string? ProvisioningState,
    string? RunningState,
    string? HealthState,
    int Replicas,
    string? Error)
{
    public static AcaRevisionStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;
        return new AcaRevisionStatus(
            props.ValueKind == JsonValueKind.Object && props.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
            AcaJson.Str(props, "provisioningState"),
            AcaJson.Str(props, "runningState"),
            AcaJson.Str(props, "healthState"),
            props.ValueKind == JsonValueKind.Object && props.TryGetProperty("replicas", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32() : 0,
            AcaJson.Str(props, "provisioningError") ?? AcaJson.Str(props, "runningStateDetails"));
    }
}

internal static class AcaJson
{
    public static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// Turns an app and one of its revisions into a health observation. A revision is healthy once
/// it is provisioned, active, running, not reported unhealthy, and the app has made it its latest
/// ready revision (so traffic has moved to it).
/// </summary>
internal static class AcaRolloutHealth
{
    public static HealthObservation Evaluate(AcaAppStatus? app, AcaRevisionStatus? revision, string revisionName)
    {
        if (app is null)
        {
            return HealthObservation.Failed("the container app does not exist");
        }

        if (app.MultipleRevisions && app.Traffic.Count > 0)
        {
            // Several revisions can be active: what counts is that this one has all the traffic.
            var candidate = EvaluateCandidate(app, revision, revisionName);
            var weight = app.WeightOf(revisionName);
            return candidate.State == HealthState.Healthy && weight < 100
                ? HealthObservation.Progressing($"{candidate.Detail}, {weight}% of the traffic")
                : candidate;
        }

        if (app.LatestRevision is { } latest && latest != revisionName)
        {
            return HealthObservation.Failed($"revision {latest} replaced {revisionName}");
        }

        if (revision is null)
        {
            return HealthObservation.Progressing($"waiting for revision {revisionName}");
        }

        if (revision.ProvisioningState == "Failed" || revision.RunningState == "Failed")
        {
            return HealthObservation.Failed(revision.Error ?? $"revision {revisionName} failed");
        }

        var detail = $"{revision.RunningState ?? "starting"}, health {revision.HealthState ?? "unknown"}, " +
                     $"{revision.Replicas} replica(s)";
        var running = revision.RunningState is "Running" or "RunningAtMaxScale" or "ScaledToZero";
        return revision.ProvisioningState == "Provisioned" && revision.Active && running &&
               revision.HealthState != "Unhealthy" && app.LatestReadyRevision == revisionName
            ? HealthObservation.Healthy(detail)
            : HealthObservation.Progressing(detail);
    }

    /// <summary>
    /// A revision running next to others (a canary or blue-green candidate): healthy once it is
    /// provisioned, active, running and not reported unhealthy, whatever traffic it gets.
    /// </summary>
    public static HealthObservation EvaluateCandidate(AcaAppStatus? app, AcaRevisionStatus? revision, string revisionName)
    {
        if (app is null)
        {
            return HealthObservation.Failed("the container app does not exist");
        }

        if (revision is null)
        {
            return HealthObservation.Progressing($"waiting for revision {revisionName}");
        }

        if (revision.ProvisioningState == "Failed" || revision.RunningState == "Failed")
        {
            return HealthObservation.Failed(revision.Error ?? $"revision {revisionName} failed");
        }

        if (revision.HealthState == "Unhealthy")
        {
            return HealthObservation.Failed(revision.Error ?? $"revision {revisionName} is unhealthy");
        }

        var detail = $"{revision.RunningState ?? "starting"}, health {revision.HealthState ?? "unknown"}, " +
                     $"{revision.Replicas} replica(s)";
        var running = revision.RunningState is "Running" or "RunningAtMaxScale" or "ScaledToZero";
        return revision.ProvisioningState == "Provisioned" && revision.Active && running
            ? HealthObservation.Healthy(detail)
            : HealthObservation.Progressing(detail);
    }
}
