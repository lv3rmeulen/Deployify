using System.Collections.Generic;

namespace Deployify.Core.Rollouts;

public enum RolloutOutcome
{
    /// <summary>Every service deployed and passed its health gate.</summary>
    Succeeded,

    /// <summary>The rollout failed and nothing was rolled back (disabled, or nothing had changed).</summary>
    Failed,

    /// <summary>The rollout failed and every touched service is back on its previous revision.</summary>
    RolledBack,

    /// <summary>The rollout failed and at least one service could not be restored. Needs a human.</summary>
    RollbackFailed
}

public sealed class RolloutResult
{
    public RolloutOutcome Outcome { get; init; }
    public string? Reason { get; init; }
    public string RolloutId { get; init; } = string.Empty;
    public string JournalPath { get; init; } = string.Empty;
    public IReadOnlyList<ServiceRolloutRecord> Services { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>0 succeeded, 1 failed, 2 rolled back, 3 rollback failed.</summary>
    public int ExitCode => Outcome switch
    {
        RolloutOutcome.Succeeded => 0,
        RolloutOutcome.RolledBack => 2,
        RolloutOutcome.RollbackFailed => 3,
        _ => 1
    };
}

public enum RolloutProgressKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>A line of progress for the console.</summary>
public sealed record RolloutProgress(RolloutProgressKind Kind, string? Service, string Message);
