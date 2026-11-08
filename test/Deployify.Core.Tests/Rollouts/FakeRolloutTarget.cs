using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using NSubstitute;

namespace Deployify.Core.Tests.Rollouts;

/// <summary>
/// An <see cref="IRolloutTarget"/> that records every call. A service's previous revision is
/// "{name}:1" and the deployed one "{name}:2"; every revision is healthy unless scripted.
/// </summary>
internal sealed class FakeRolloutTarget : IRolloutTarget
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, ServiceSnapshot> Snapshots { get; } = [];
    public Dictionary<string, AppliedRevision> ApplyResults { get; } = [];
    public HashSet<string> FailRestore { get; } = [];
    public bool NativeRollback { get; set; }

    /// <summary>Observations per revision, returned in order; the last one repeats.</summary>
    public Dictionary<string, Queue<HealthObservation>> Health { get; } = [];

    /// <summary>Runs on every observation, e.g. to cancel the rollout part-way.</summary>
    public Action<string, string>? OnObserve { get; set; }

    public FakeRolloutTarget Script(string revision, params HealthObservation[] observations)
    {
        Health[revision] = new Queue<HealthObservation>(observations);
        return this;
    }

    public Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add($"capture {service}");
        return Task.FromResult(Snapshots.TryGetValue(service, out var s)
            ? s
            : new ServiceSnapshot { ServiceName = service, Existed = true, Revision = $"{service}:1" });
    }

    public Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add($"apply {service}");
        return Task.FromResult(ApplyResults.TryGetValue(service, out var r)
            ? r
            : new AppliedRevision { ServiceName = service, Success = true, Revision = $"{service}:2" });
    }

    public Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add($"observe {revision}");
        OnObserve?.Invoke(service, revision);
        if (Health.TryGetValue(revision, out var q) && q.Count > 0)
        {
            return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
        }

        return Task.FromResult(HealthObservation.Healthy("ok"));
    }

    /// <summary>Revision a restore reports, per service; defaults to the snapshot's own.</summary>
    public Dictionary<string, string> RestoredRevisions { get; } = [];

    public Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add($"restore {snapshot.ServiceName}");
        if (FailRestore.Contains(snapshot.ServiceName))
        {
            throw new InvalidOperationException("restore exploded");
        }

        if (!snapshot.Existed)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(
            RestoredRevisions.TryGetValue(snapshot.ServiceName, out var r) ? r : snapshot.Revision);
    }

    public bool HasNativeRollback(EnvironmentConfig config) => NativeRollback;

    /// <summary>
    /// A deployment target with the given services; <paramref name="dependsOn"/> maps a service
    /// to the services it uses.
    /// </summary>
    public static IDeploymentTarget Target(
        string[] services, Dictionary<string, string[]>? dependsOn = null, string imageTag = "v1")
    {
        IReadOnlyList<ServiceExplanation> explanations =
            [.. services.Select(s => new ServiceExplanation { Name = s, Image = $"registry/{s}", ImageTag = imageTag })];
        IReadOnlyList<ServiceDependency> edges =
            [.. (dependsOn ?? new Dictionary<string, string[]>()).SelectMany(kv => kv.Value.Select(d => new ServiceDependency { From = d, To = kv.Key }))];

        var target = Substitute.For<IDeploymentTarget>();
        target.DisplayName.Returns("Fake");
        target.Explain(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>()).Returns(explanations);
        target.GetDependencies(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>()).Returns(edges);
        return target;
    }
}
