using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

/// <summary>Who moves the traffic in a canary or blue-green rollout.</summary>
public enum RolloutStrategyMode
{
    /// <summary>
    /// Deployify does: it starts the new version next to the old one, shifts traffic step by step,
    /// watches health and goes back on any failure. The target implements
    /// <see cref="ITrafficShiftingTarget"/>.
    /// </summary>
    Driven,

    /// <summary>
    /// The platform does (ECS deployment strategies, Argo Rollouts, Flagger): the target writes the
    /// strategy into what it deploys, and Deployify only waits for the platform to finish.
    /// </summary>
    Native
}

/// <summary>How closely a target can follow the weights of a canary's steps.</summary>
public enum TrafficPrecision
{
    /// <summary>Weights are exact traffic percentages (a revision traffic split, weighted target groups).</summary>
    Exact,

    /// <summary>Weights are met by running more or fewer instances of the new version, so they are rounded.</summary>
    Instances,

    /// <summary>The new version's share is set once, by how many instances it starts with; later steps only wait.</summary>
    Fixed
}

/// <summary>
/// Optional capability of an <see cref="IDeploymentTarget"/>: deploy services with the Canary
/// and BlueGreen strategies of <see cref="RolloutConfig"/>. A target without it deploys every
/// service with its rolling update and refuses an environment that asks for another strategy.
/// </summary>
public interface IRolloutStrategyTarget
{
    RolloutStrategyMode StrategyMode { get; }

    /// <summary>
    /// Why <paramref name="service"/> can't be deployed with <paramref name="strategy"/> in this
    /// environment (the strategy isn't supported, or a setting it needs is missing), or null when
    /// it can. Must not change anything; may read the platform.
    /// </summary>
    Task<string?> GetStrategyUnsupportedReasonAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, RolloutStrategy strategy,
        CancellationToken ct = default);
}

/// <summary>
/// A target on which Deployify runs canary and blue-green rollouts itself (see
/// <see cref="RolloutStrategyMode.Driven"/>). <c>RolloutEngine</c> calls, for one service that
/// already exists: <see cref="StartCandidateAsync"/>, then <see cref="ShiftTrafficAsync"/> once per
/// step, then <see cref="PromoteAsync"/>, then <see cref="FinishAsync"/> once the promoted revision
/// is healthy. Going back is always <see cref="IRolloutTarget.RestoreAsync"/>, which must also
/// remove a candidate and send all traffic back to the snapshot's revision, from any point of the
/// rollout and from a later process (<c>deployify rollback</c>).
/// </summary>
public interface ITrafficShiftingTarget : IRolloutStrategyTarget, IRolloutTarget
{
    TrafficPrecision Precision { get; }

    /// <summary>
    /// Starts the new version of <paramref name="service"/> next to the running one, with no
    /// traffic where the platform allows it (<see cref="TrafficPrecision.Fixed"/> targets start it
    /// with its fixed share). Returns the candidate revision, which <see cref="ObserveCandidateAsync"/>
    /// watches, and a preview address when the platform gives one.
    /// </summary>
    Task<AppliedRevision> StartCandidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        RolloutStrategy strategy, ServiceSnapshot before, CancellationToken ct = default);

    /// <summary>One look at the candidate's health. Like <see cref="IRolloutTarget.ObserveAsync"/>, it must not block.</summary>
    Task<HealthObservation> ObserveCandidateAsync(
        EnvironmentConfig config, string service, string candidate, CancellationToken ct = default);

    /// <summary>
    /// Sends <paramref name="percent"/> of the traffic (1 to 100) to the candidate and the rest to the
    /// running version. Returns the share the candidate really gets, which differs from
    /// <paramref name="percent"/> unless <see cref="Precision"/> is <see cref="TrafficPrecision.Exact"/>.
    /// </summary>
    Task<int> ShiftTrafficAsync(
        EnvironmentConfig config, string service, string candidate, int percent, CancellationToken ct = default);

    /// <summary>
    /// Makes the candidate the service's version for good, leaving the service as a plain apply
    /// would have. Returns the revision now rolling out, which the engine watches with
    /// <see cref="IRolloutTarget.ObserveAsync"/>, or null when nothing more has to become healthy.
    /// </summary>
    Task<string?> PromoteAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, string candidate,
        CancellationToken ct = default);

    /// <summary>
    /// Clean-up once the promoted revision is healthy: remove the candidate's own objects and the
    /// old version. A failure here is a warning; the service is already on its new version.
    /// </summary>
    Task FinishAsync(EnvironmentConfig config, string service, string candidate, CancellationToken ct = default);
}
