using System;

namespace Deployify.Core.Rollouts;

/// <summary>Timeouts and thresholds for a health-gated rollout.</summary>
public sealed class RolloutOptions
{
    /// <summary>How long one service may take to become healthy before it counts as failed.</summary>
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Time between two health observations of the same service.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Upper bound for a single observation (one CLI or API call). A slow call counts as
    /// "still progressing", not as a failure.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Healthy observations needed in a row before a service passes. Any other observation
    /// resets the count, which catches services that come up and then crash.
    /// </summary>
    public int RequiredSuccesses { get; init; } = 2;

    /// <summary>When false, a failed rollout is left as it is for inspection.</summary>
    public bool AutoRollback { get; init; } = true;

    /// <summary>
    /// Time budget for the whole rollback, restores and their health checks included. It is
    /// separate from the rollout's own cancellation, so a timeout or Ctrl+C never stops a rollback.
    /// </summary>
    public TimeSpan RollbackTimeout { get; init; } = TimeSpan.FromMinutes(15);
}
