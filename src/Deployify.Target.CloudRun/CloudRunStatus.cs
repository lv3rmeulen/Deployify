using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;

namespace Deployify.Target.CloudRun;

/// <summary>One entry of a service's traffic split.</summary>
public sealed record CloudRunTraffic(string? RevisionName, bool LatestRevision, int Percent, string? Tag, string? Url);

/// <summary>A <c>Ready</c>-style condition: <c>True</c>, <c>False</c> or <c>Unknown</c>.</summary>
public sealed record CloudRunCondition(string Status, string? Reason, string? Message);

/// <summary>The parts of <c>gcloud run services describe --format=json</c> Deployify reads.</summary>
public sealed record CloudRunServiceStatus(
    string Name,
    long Generation,
    long ObservedGeneration,
    string? LatestCreatedRevision,
    string? LatestReadyRevision,
    string? Url,
    CloudRunCondition? Ready,
    IReadOnlyList<CloudRunTraffic> Traffic,
    IReadOnlyDictionary<string, string> Labels)
{
    /// <summary>The percentage of the traffic <paramref name="revision"/> gets.</summary>
    public int PercentOf(string revision)
        => Traffic.Where(t => t.RevisionName == revision || (t.LatestRevision && t.RevisionName is null && LatestReadyRevision == revision))
            .Sum(t => t.Percent);

    /// <summary>The revision that has all the traffic, or null when it is split.</summary>
    public string? Serving
        => Traffic.Where(t => t.Percent > 0).ToList() is [var only] && only.Percent == 100
            ? only.RevisionName ?? LatestReadyRevision
            : null;

    public static CloudRunServiceStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var metadata = Obj(root, "metadata");
        var status = Obj(root, "status");
        var traffic = new List<CloudRunTraffic>();
        if (status is { } s && s.TryGetProperty("traffic", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in t.EnumerateArray())
            {
                traffic.Add(new CloudRunTraffic(
                    Str(entry, "revisionName"),
                    entry.TryGetProperty("latestRevision", out var l) && l.ValueKind == JsonValueKind.True,
                    entry.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                    Str(entry, "tag"),
                    Str(entry, "url")));
            }
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is { } m && Obj(m, "labels") is { } l2)
        {
            foreach (var p in l2.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                labels[p.Name] = p.Value.GetString()!;
            }
        }

        return new CloudRunServiceStatus(
            metadata is { } md ? Str(md, "name") ?? "" : "",
            metadata is { } mg ? Long(mg, "generation") : 0,
            status is { } so ? Long(so, "observedGeneration") : 0,
            status is { } sc ? Str(sc, "latestCreatedRevisionName") : null,
            status is { } sr ? Str(sr, "latestReadyRevisionName") : null,
            status is { } su ? Str(su, "url") : null,
            ReadyCondition(status),
            traffic,
            labels);
    }

    internal static CloudRunCondition? ReadyCondition(JsonElement? status)
    {
        if (status is not { } s || !s.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var c in conditions.EnumerateArray())
        {
            if (Str(c, "type") == "Ready")
            {
                return new CloudRunCondition(Str(c, "status") ?? "Unknown", Str(c, "reason"), Str(c, "message"));
            }
        }

        return null;
    }

    internal static JsonElement? Obj(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}

/// <summary>The <c>Ready</c> condition of one revision (<c>gcloud run revisions describe</c>).</summary>
public sealed record CloudRunRevisionStatus(string Name, CloudRunCondition? Ready)
{
    public static CloudRunRevisionStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = CloudRunServiceStatus.Obj(root, "metadata") is { } m ? CloudRunServiceStatus.Str(m, "name") ?? "" : "";
        return new CloudRunRevisionStatus(name, CloudRunServiceStatus.ReadyCondition(CloudRunServiceStatus.Obj(root, "status")));
    }
}

/// <summary>
/// Health of a Cloud Run rollout. A revision Deployify deployed with the generated spec is
/// identified by the service's <c>metadata.generation</c> right after the change (as on
/// Kubernetes: Cloud Run only names the new revision once it has seen the change); a revision
/// a restore went back to is identified by its name.
/// </summary>
public static class CloudRunHealth
{
    /// <summary>True when <paramref name="revision"/> is a generation rather than a revision name.</summary>
    public static bool IsGeneration(string revision, out long generation)
        => long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out generation);

    /// <summary>Generation <paramref name="generation"/> is rolled out and its revision has all the traffic.</summary>
    public static HealthObservation Evaluate(CloudRunServiceStatus? service, long generation)
    {
        if (service is null)
        {
            return HealthObservation.Failed("the Cloud Run service does not exist");
        }

        if (service.Generation > generation)
        {
            return HealthObservation.Failed($"the service was changed again (generation {service.Generation})");
        }

        if (service.ObservedGeneration < generation)
        {
            return HealthObservation.Progressing("waiting for Cloud Run to pick up the change");
        }

        switch (service.Ready?.Status)
        {
            case "False":
                return HealthObservation.Failed(service.Ready.Message ?? service.Ready.Reason ?? "the service is not ready");
            case "True" when service.LatestReadyRevision is { } ready && ready == service.LatestCreatedRevision:
                var percent = service.PercentOf(ready);
                return percent >= 100
                    ? HealthObservation.Healthy($"revision {ready} serving")
                    : HealthObservation.Progressing($"revision {ready} has {percent}% of the traffic");
            default:
                return HealthObservation.Progressing(service.Ready?.Message ?? $"deploying {service.LatestCreatedRevision ?? "a new revision"}");
        }
    }

    /// <summary>A named revision is ready and has all the traffic.</summary>
    public static HealthObservation EvaluateRevision(CloudRunServiceStatus? service, CloudRunRevisionStatus? revision, string name)
    {
        if (service is null)
        {
            return HealthObservation.Failed("the Cloud Run service does not exist");
        }

        var ready = Ready(revision, name);
        if (ready.State != HealthState.Healthy)
        {
            return ready;
        }

        var percent = service.PercentOf(name);
        return percent >= 100
            ? HealthObservation.Healthy($"revision {name} serving")
            : HealthObservation.Progressing($"revision {name} has {percent}% of the traffic");
    }

    /// <summary>The candidate of generation <paramref name="generation"/>: its revision is ready, whatever traffic it gets.</summary>
    public static HealthObservation EvaluateCandidate(
        CloudRunServiceStatus? service, CloudRunRevisionStatus? revision, long generation, string? stable)
    {
        if (service is null)
        {
            return HealthObservation.Failed("the Cloud Run service does not exist");
        }

        if (service.ObservedGeneration < generation || service.LatestCreatedRevision is null)
        {
            return HealthObservation.Progressing("waiting for Cloud Run to create the new revision");
        }

        if (service.LatestCreatedRevision == stable)
        {
            return HealthObservation.Healthy("no new revision (only the configuration changed)");
        }

        return Ready(revision, service.LatestCreatedRevision);
    }

    private static HealthObservation Ready(CloudRunRevisionStatus? revision, string name)
        => revision?.Ready?.Status switch
        {
            null => HealthObservation.Progressing($"waiting for revision {name}"),
            "True" => HealthObservation.Healthy($"revision {name} ready"),
            "False" => HealthObservation.Failed(revision.Ready.Message ?? revision.Ready.Reason ?? $"revision {name} failed"),
            _ => HealthObservation.Progressing(revision.Ready.Message ?? $"revision {name} starting")
        };
}
