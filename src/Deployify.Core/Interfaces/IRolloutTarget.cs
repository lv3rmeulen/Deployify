using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

/// <summary>
/// Optional capability of an <see cref="IDeploymentTarget"/>: deploy one service at a time,
/// report whether it became healthy, and put back what ran before. A target that implements
/// it gets health-gated rollouts with automatic rollback from <c>RolloutEngine</c>; the engine
/// owns ordering, polling, timeouts and the verdict, the target only talks to its platform.
/// </summary>
public interface IRolloutTarget
{
    /// <summary>
    /// Records what is running for <paramref name="service"/> right now, before anything is
    /// changed. The snapshot is written to the rollout journal, so it must never hold a secret
    /// value: store references (an ARN, a job version, an image digest), not contents.
    /// </summary>
    Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default);

    /// <summary>
    /// Deploys one service from the artifacts plan wrote, without waiting for it to become
    /// healthy. Returns the revision that was deployed, which <see cref="ObserveAsync"/> then
    /// watches.
    /// </summary>
    Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default);

    /// <summary>
    /// Takes one look at <paramref name="revision"/> of <paramref name="service"/>. It must not
    /// block or poll: the engine calls it repeatedly and applies its own timeouts.
    /// </summary>
    Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default);

    /// <summary>
    /// Puts the service back the way <paramref name="snapshot"/> recorded it. For a service that
    /// did not exist before, the target decides what "back" means (for example scaling it to zero).
    /// Must be safe to call more than once. Returns the revision now rolling out, which the engine
    /// waits on: the snapshot's own revision where revisions are immutable (an ECS task definition),
    /// or a new one where restoring creates it (a Nomad revert makes a new job version). Null when
    /// there is nothing to wait for.
    /// </summary>
    Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// True when the platform reverts a failed deployment by itself (ECS deployment circuit
    /// breaker, Nomad <c>auto_revert</c>). The engine then waits for the platform instead of
    /// restoring a second time.
    /// </summary>
    bool HasNativeRollback(EnvironmentConfig config);

    /// <summary>
    /// Why a health-gated rollout can't run in this environment right now (for example a CLI it
    /// needs is missing), or null when it can. The caller then deploys the plain way.
    /// </summary>
    Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Runs once after every service passed its health gate, for clean-up a whole-project apply
    /// would have done (Compose removes containers of services no longer in the manifest). A
    /// failure here is reported as a warning; the rollout itself has already succeeded.
    /// </summary>
    Task CompleteAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>What ran for one service before a rollout touched it.</summary>
public sealed class ServiceSnapshot
{
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>False when the service did not exist yet, so there is nothing to go back to.</summary>
    public bool Existed { get; init; }

    /// <summary>The running revision (task definition ARN, job version, …), or null when none.</summary>
    public string? Revision { get; init; }

    /// <summary>Anything else the target needs to restore, such as the desired count. No secrets.</summary>
    public Dictionary<string, string> Data { get; init; } = [];
}

/// <summary>The result of deploying one service.</summary>
public sealed class AppliedRevision
{
    public string ServiceName { get; init; } = string.Empty;
    public bool Success { get; init; }

    /// <summary>The revision now rolling out, which the health gate watches.</summary>
    public string? Revision { get; init; }

    public string? Error { get; init; }
}

public enum HealthState
{
    /// <summary>Still rolling out; look again later.</summary>
    Progressing,

    /// <summary>The revision is serving and stable.</summary>
    Healthy,

    /// <summary>The revision will not become healthy; roll back.</summary>
    Failed,

    /// <summary>The platform already reverted to an earlier revision on its own.</summary>
    RevertedByPlatform
}

/// <summary>
/// One look at a revision's health, with a short human-readable reason. For
/// <see cref="HealthState.RevertedByPlatform"/>, <see cref="Revision"/> is the revision the
/// platform went back to, when it is known; the engine then waits for that one.
/// </summary>
public sealed record HealthObservation(HealthState State, string? Detail = null, string? Revision = null)
{
    public static HealthObservation Progressing(string? detail = null) => new(HealthState.Progressing, detail);
    public static HealthObservation Healthy(string? detail = null) => new(HealthState.Healthy, detail);
    public static HealthObservation Failed(string detail) => new(HealthState.Failed, detail);

    public static HealthObservation Reverted(string detail, string? revertedTo = null)
        => new(HealthState.RevertedByPlatform, detail, revertedTo);
}
