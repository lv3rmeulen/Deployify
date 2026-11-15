using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Deployify.Core.ResourceSources;

namespace Deployify.Target.Nomad;

/// <summary>A Deployify job as listed by GET /v1/jobs.</summary>
internal sealed record NomadJobListing(string Id, string Status, bool Stop, long JobModifyIndex, long? SubmitTime);

/// <summary>Where a dashboard resource lives in Nomad, for its logs and commands.</summary>
/// <param name="JobId">The job.</param>
/// <param name="AllocId">The allocation, for an allocation resource; null for the job itself.</param>
/// <param name="Task">The task whose logs are shown (the job's main task, named like the job).</param>
/// <param name="ActiveAllocs">The job's allocations that should be running, for job-wide logs and restart.</param>
internal sealed record NomadResourceLocation(string JobId, string? AllocId, string Task, IReadOnlyList<string> ActiveAllocs);

/// <summary>
/// Turns Nomad's job and allocation JSON into dashboard resources: a "NomadJob" per Deployify
/// service, with its allocations nested under it. Pure, so it is tested without a Nomad agent.
/// </summary>
internal static class NomadResourceMapper
{
    public const string JobType = "NomadJob";
    public const string AllocationType = "NomadAllocation";

    /// <summary>Command that stops one allocation so the scheduler replaces it.</summary>
    public const string RescheduleCommand = "reschedule";

    /// <summary>Deployify's jobs from GET /v1/jobs?meta=true (the ones with its meta key).</summary>
    public static IReadOnlyList<NomadJobListing> ManagedJobs(string jobListJson)
    {
        using var doc = JsonDocument.Parse(jobListJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. doc.RootElement.EnumerateArray()
                .Where(j => j.TryGetProperty("Meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                            meta.TryGetProperty(NomadClient.ManagedMetaKey, out _))
                .Select(j => new NomadJobListing(
                    Str(j, "ID") ?? "",
                    Str(j, "Status") ?? "",
                    Bool(j, "Stop"),
                    Long(j, "JobModifyIndex") ?? 0,
                    Long(j, "SubmitTime")))
                .Where(j => j.Id.Length > 0)
                .OrderBy(j => j.Id, StringComparer.Ordinal)
        ];
    }

    /// <param name="jobs">Deployify's jobs.</param>
    /// <param name="jobDetails">GET /v1/job/:id JSON by job id; a job without one is shown from its listing alone.</param>
    /// <param name="allocationsJson">GET /v1/allocations?resources=true for the namespace.</param>
    public static (IReadOnlyList<ResourceSnapshot> Resources, IReadOnlyDictionary<string, NomadResourceLocation> Locations) Map(
        IReadOnlyList<NomadJobListing> jobs,
        IReadOnlyDictionary<string, string> jobDetails,
        string allocationsJson,
        string @namespace,
        ResourceSourceOptions options)
    {
        using var allocDoc = JsonDocument.Parse(allocationsJson);
        var allAllocs = allocDoc.RootElement.ValueKind == JsonValueKind.Array
            ? allocDoc.RootElement.EnumerateArray().ToList()
            : [];

        var resources = new List<ResourceSnapshot>();
        var locations = new Dictionary<string, NomadResourceLocation>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            using var detailDoc = jobDetails.TryGetValue(job.Id, out var json) ? JsonDocument.Parse(json) : null;
            var detail = detailDoc?.RootElement;

            // Only the allocations meant to run: old versions' finished ones are history, not resources.
            var active = allAllocs
                .Where(a => Str(a, "JobID") == job.Id && Str(a, "DesiredStatus") == "run")
                .OrderBy(a => Str(a, "Name"), StringComparer.Ordinal)
                .ThenBy(a => Str(a, "ID"), StringComparer.Ordinal)
                .ToList();
            var activeIds = active.Select(a => Str(a, "ID")!).ToList();
            var task = MainTask(detail, job.Id) is { } t ? Str(t, "Name") ?? job.Id : job.Id;

            locations[job.Id] = new NomadResourceLocation(job.Id, null, task, activeIds);
            resources.Add(MapJob(job, detail, active, @namespace, options));

            foreach (var alloc in active)
            {
                var id = Str(alloc, "ID")!;
                var name = AllocationName(job.Id, id);
                locations[name] = new NomadResourceLocation(job.Id, id, task, [id]);
                resources.Add(MapAllocation(name, job.Id, alloc));
            }
        }

        return (resources, locations);
    }

    /// <summary>"api-1a2b3c4d": the job and the start of the allocation id, like the Nomad UI shows it.</summary>
    public static string AllocationName(string jobId, string allocId) => $"{jobId}-{allocId[..Math.Min(8, allocId.Length)]}";

    private static ResourceSnapshot MapJob(
        NomadJobListing job, JsonElement? detail, IReadOnlyList<JsonElement> active, string @namespace, ResourceSourceOptions options)
    {
        var group = detail is { } d ? Arr(d, "TaskGroups").FirstOrDefault() : default;
        var count = group.ValueKind == JsonValueKind.Object ? Int(group, "Count") ?? active.Count : active.Count;
        var stopped = job.Stop || job.Status == "dead";
        var running = active.Count(a => Str(a, "ClientStatus") == "running");
        var unhealthy = active.Count(a => Bool(a, "DeploymentStatus", "Healthy") is false && Has(a, "DeploymentStatus", "Healthy"));
        var pending = active.Count(a => Str(a, "ClientStatus") == "pending");
        var failed = active.Count(a => Str(a, "ClientStatus") is "failed" or "lost" || TaskStates(a).Any(ts => Bool(ts, "Failed")));

        string state;
        ResourceStateStyle? style;
        if (stopped || count == 0)
        {
            (state, style) = running > 0 ? (ResourceStates.Stopping, ResourceStateStyle.Info) : (ResourceStates.Exited, (ResourceStateStyle?)null);
        }
        else if (failed > 0 && running == 0)
        {
            (state, style) = (ResourceStates.FailedToStart, ResourceStateStyle.Error);
        }
        else if (running >= count && unhealthy == 0)
        {
            (state, style) = (ResourceStates.Running, ResourceStateStyle.Success);
        }
        else if (running > 0 && pending == 0)
        {
            (state, style) = (ResourceStates.RuntimeUnhealthy, ResourceStateStyle.Warning);
        }
        else
        {
            (state, style) = (ResourceStates.Starting, ResourceStateStyle.Info);
        }

        var health = new List<ResourceHealthReport>();
        if (!stopped && count > 0)
        {
            var healthy = running - unhealthy;
            health.Add(new ResourceHealthReport("allocations",
                healthy >= count ? ResourceHealthStatus.Healthy : healthy > 0 ? ResourceHealthStatus.Degraded : ResourceHealthStatus.Unhealthy,
                $"{healthy}/{count} allocations running and healthy"));
        }

        var task = MainTask(detail, job.Id);
        var properties = new List<ResourceProperty>
        {
            new("nomad.namespace", @namespace, "Namespace"),
            new("nomad.version", detail is { } v ? Long(v, "Version")?.ToString(CultureInfo.InvariantCulture) : null, "Job version"),
            new("nomad.count", $"{running}/{count}", "Running allocations"),
            new("nomad.status", job.Status, "Job status")
        };
        if (task is { } tk)
        {
            properties.Add(new ResourceProperty("nomad.driver", Str(tk, "Driver"), "Driver"));
            if (Str(tk, "Config", "image") is { } image)
            {
                properties.Add(new ResourceProperty(ResourcePropertyNames.ContainerImage, image, "Image"));
            }
            else if (Str(tk, "Config", "command") is { } command)
            {
                properties.Add(new ResourceProperty("nomad.command", command, "Command"));
            }
        }

        if (detail is { } dc)
        {
            properties.Add(new ResourceProperty("nomad.datacenters",
                string.Join(", ", Arr(dc, "Datacenters").Select(x => x.GetString())), "Datacenters"));
        }

        return new ResourceSnapshot
        {
            Name = job.Id,
            DisplayName = job.Id,
            ResourceType = JobType,
            State = state,
            StateStyle = style,
            CreatedAt = job.SubmitTime is { } submitted ? FromUnixNanos(submitted) : null,
            Urls = [.. active.Where(a => Str(a, "ClientStatus") == "running").SelectMany(Urls).DistinctBy(u => u.Url)],
            Environment = EnvironmentOf(task, options),
            Properties = properties,
            HealthReports = health,
            Commands =
            [
                ResourceCommandNames.StartCommand(enabled: stopped),
                ResourceCommandNames.StopCommand(enabled: !stopped,
                    confirmation: $"Stop job {job.Id}? The next 'deployify apply' starts it again."),
                ResourceCommandNames.RestartCommand(enabled: !stopped && active.Count > 0,
                    description: "Restart the tasks of every allocation in place")
            ]
        };
    }

    private static ResourceSnapshot MapAllocation(string name, string jobId, JsonElement alloc)
    {
        var clientStatus = Str(alloc, "ClientStatus") ?? "unknown";
        var healthy = Has(alloc, "DeploymentStatus", "Healthy") ? Bool(alloc, "DeploymentStatus", "Healthy") : (bool?)null;
        var taskStates = TaskStates(alloc).ToList();
        var restarts = taskStates.Sum(ts => Int(ts, "Restarts") ?? 0);

        var (state, style) = clientStatus switch
        {
            "running" when healthy is false => (ResourceStates.RuntimeUnhealthy, ResourceStateStyle.Warning),
            "running" => (ResourceStates.Running, ResourceStateStyle.Success),
            "pending" => (ResourceStates.Starting, ResourceStateStyle.Info),
            "complete" => (ResourceStates.Finished, (ResourceStateStyle?)null),
            "failed" => (ResourceStates.Exited, ResourceStateStyle.Error),
            "lost" or "unknown" => (ResourceStates.Exited, ResourceStateStyle.Error),
            _ => (clientStatus, (ResourceStateStyle?)null)
        };

        var health = new List<ResourceHealthReport>();
        if (clientStatus == "running" && healthy is not null)
        {
            health.Add(new ResourceHealthReport("deployment",
                healthy.Value ? ResourceHealthStatus.Healthy : ResourceHealthStatus.Unhealthy,
                healthy.Value ? "Healthy" : "Failed its health checks"));
        }

        var allocId = Str(alloc, "ID")!;
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            ResourceType = AllocationType,
            Parent = jobId,
            State = state,
            StateStyle = style,
            CreatedAt = Long(alloc, "CreateTime") is { } created ? FromUnixNanos(created) : null,
            StartedAt = taskStates.Select(ts => Time(ts, "StartedAt")).Where(t => t is not null).Min(),
            StoppedAt = clientStatus is "complete" or "failed" or "lost"
                ? taskStates.Select(ts => Time(ts, "FinishedAt")).Where(t => t is not null).Max()
                : null,
            Urls = Urls(alloc),
            Properties =
            [
                new("nomad.allocation", allocId, "Allocation ID"),
                new("nomad.node", Str(alloc, "NodeName"), "Node"),
                new("nomad.version", Long(alloc, "JobVersion")?.ToString(CultureInfo.InvariantCulture), "Job version"),
                new("nomad.restarts", restarts.ToString(CultureInfo.InvariantCulture), "Restarts")
            ],
            HealthReports = health,
            Commands =
            [
                ResourceCommandNames.RestartCommand(enabled: clientStatus == "running",
                    description: "Restart the allocation's tasks in place"),
                new ResourceCommandInfo
                {
                    Name = RescheduleCommand,
                    DisplayName = "Reschedule",
                    Description = "Stop this allocation; the scheduler places a replacement",
                    IconName = "ArrowSync",
                    ConfirmationMessage = $"Stop allocation {name}? Nomad places a replacement, possibly on another node.",
                    Enabled = clientStatus is "running" or "pending"
                }
            ]
        };
    }

    private static IReadOnlyList<ResourceUrl> Urls(JsonElement alloc)
    {
        // Deployify's jobs use host networking with static ports: the host IP and port are the address.
        var urls = new List<ResourceUrl>();
        foreach (var port in Arr(alloc, "AllocatedResources", "Shared", "Ports"))
        {
            var label = Str(port, "Label");
            var host = Str(port, "HostIP");
            var value = Int(port, "Value");
            if (host is null || value is null or 0)
            {
                continue;
            }

            var hostPart = host.Contains(':') ? $"[{host}]" : host;
            urls.Add(new ResourceUrl(label, $"{SchemeFor(label, value.Value)}://{hostPart}:{value}"));
        }

        return urls;
    }

    private static string SchemeFor(string? label, int port)
    {
        var hint = (label ?? "").ToLowerInvariant();
        if (hint.StartsWith("https", StringComparison.Ordinal) || port == 443)
        {
            return "https";
        }

        return hint.StartsWith("http", StringComparison.Ordinal) || port is 80 or 8080 ? "http" : "tcp";
    }

    private static IReadOnlyList<ResourceEnvironmentVariable> EnvironmentOf(JsonElement? task, ResourceSourceOptions options)
    {
        if (task is not { } t || !t.TryGetProperty("Env", out var env) || env.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return
        [
            .. env.EnumerateObject()
                .Select(p => new ResourceEnvironmentVariable(p.Name, options.ShowEnvironmentValues ? p.Value.GetString() : null))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
        ];
    }

    /// <summary>The job's own task (named like the job), not a sidecar such as "{job}-dapr".</summary>
    private static JsonElement? MainTask(JsonElement? detail, string jobId)
    {
        if (detail is not { } d)
        {
            return null;
        }

        var tasks = Arr(d, "TaskGroups").SelectMany(g => Arr(g, "Tasks")).ToList();
        return tasks.FirstOrDefault(t => Str(t, "Name") == jobId) is { ValueKind: JsonValueKind.Object } named
            ? named
            : tasks.Count > 0 ? tasks[0] : null;
    }

    private static IEnumerable<JsonElement> TaskStates(JsonElement alloc)
        => alloc.TryGetProperty("TaskStates", out var states) && states.ValueKind == JsonValueKind.Object
            ? states.EnumerateObject().Select(p => p.Value)
            : [];

    private static DateTimeOffset FromUnixNanos(long nanos) => DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static bool TryGet(JsonElement element, string[] path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Has(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string? Str(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static IEnumerable<JsonElement> Arr(JsonElement element, params string[] path)
        => TryGet(element, path, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>Nomad writes "0001-01-01T00:00:00Z" for times that haven't happened.</summary>
    private static DateTimeOffset? Time(JsonElement element, params string[] path)
        => Str(element, path) is { } s &&
           DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) &&
           t.Year > 1970
            ? t
            : null;
}
