using System.Text.Json;
using Deployify.Core.Interfaces;

namespace Deployify.Target.AzureContainerApps;

/// <summary>The parts of <c>az containerapp show</c> the rollout needs.</summary>
public sealed record AcaAppStatus(string? LatestRevision, string? LatestReadyRevision)
{
    public static AcaAppStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.TryGetProperty("properties", out var p) ? p : default;
        return new AcaAppStatus(
            AcaJson.Str(props, "latestRevisionName"),
            AcaJson.Str(props, "latestReadyRevisionName"));
    }
}

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
}
