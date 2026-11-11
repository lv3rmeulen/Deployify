using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.Pipelines;
using Deployify.Core.Deployments;
using Microsoft.Extensions.Logging;

namespace Deployify.Hosting;

/// <summary>Sends Deployify's progress to the <c>aspire deploy</c> output: stages become tasks under the step.</summary>
internal sealed class PipelineDeploymentReporter(IReportingStep step) : IDeploymentReporter
{
    public void Info(string message) => step.Log(LogLevel.Information, message);

    public void Warn(string message) => step.Log(LogLevel.Warning, message);

    public void Error(string message) => step.Log(LogLevel.Error, message);

    public async Task<IDeploymentStage> BeginStageAsync(string description, CancellationToken ct = default)
        => new Stage(await step.CreateTaskAsync(description, ct));

    private sealed class Stage(IReportingTask task) : IDeploymentStage
    {
        public Task UpdateAsync(string message, CancellationToken ct = default) => task.UpdateAsync(message, ct);

        public Task CompleteAsync(string? message, DeploymentStageOutcome outcome = DeploymentStageOutcome.Succeeded,
            CancellationToken ct = default)
            => task.CompleteAsync(message, outcome switch
            {
                DeploymentStageOutcome.Failed => CompletionState.CompletedWithError,
                DeploymentStageOutcome.Warning => CompletionState.CompletedWithWarning,
                _ => CompletionState.Completed
            }, ct);

        public ValueTask DisposeAsync() => task.DisposeAsync();
    }
}
