using System;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.Deployments;

/// <summary>
/// Where a deployment run reports what it does. The CLI writes to the console; the Aspire
/// integration forwards to the <c>aspire deploy</c> pipeline output. Messages are plain text.
/// </summary>
public interface IDeploymentReporter
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    /// <summary>
    /// Starts a named stage (publish, plan, apply, …). Reporters without a notion of stages
    /// just log the description.
    /// </summary>
    Task<IDeploymentStage> BeginStageAsync(string description, CancellationToken ct = default)
    {
        Info(description);
        return Task.FromResult<IDeploymentStage>(new LoggingStage(this));
    }

    private sealed class LoggingStage(IDeploymentReporter reporter) : IDeploymentStage
    {
        public Task UpdateAsync(string message, CancellationToken ct = default)
        {
            reporter.Info(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string? message, DeploymentStageOutcome outcome = DeploymentStageOutcome.Succeeded,
            CancellationToken ct = default)
        {
            if (message is not null)
            {
                switch (outcome)
                {
                    case DeploymentStageOutcome.Failed:
                        reporter.Error(message);
                        break;
                    case DeploymentStageOutcome.Warning:
                        reporter.Warn(message);
                        break;
                    default:
                        reporter.Info(message);
                        break;
                }
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public enum DeploymentStageOutcome
{
    Succeeded,
    Warning,
    Failed
}

/// <summary>One stage of a deployment run, completed exactly once.</summary>
public interface IDeploymentStage : IAsyncDisposable
{
    Task UpdateAsync(string message, CancellationToken ct = default);

    Task CompleteAsync(string? message, DeploymentStageOutcome outcome = DeploymentStageOutcome.Succeeded,
        CancellationToken ct = default);
}

/// <summary>Discards everything. The default when nobody is listening.</summary>
public sealed class NullDeploymentReporter : IDeploymentReporter
{
    public static readonly NullDeploymentReporter Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}
