using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;

namespace Deployify.Core.Lifecycle;

public sealed class RemovalRequest
{
    public required IDeploymentTarget Target { get; init; }
    public required IResourceLifecycle Lifecycle { get; init; }
    public required EnvironmentConfig Config { get; init; }
    public required string EnvironmentName { get; init; }
    public required StateSession State { get; init; }
    public required RemovalPlan Plan { get; init; }
    public required RolloutOperation Operation { get; init; }
    public DeleteOptions Options { get; init; } = new();

    /// <summary>Destroy of the whole environment: also remove environment-wide leftovers at the end.</summary>
    public bool WholeEnvironment { get; init; }

    public Action<RolloutProgress>? OnProgress { get; init; }
}

public sealed class RemovalResult
{
    public bool Succeeded { get; init; }
    public List<string> Deleted { get; init; } = [];
    public string? FailedService { get; init; }
    public string? Error { get; init; }
    public List<string> EnvironmentResourcesRemoved { get; init; } = [];
    public string? JournalPath { get; init; }
    public string? JournalId { get; init; }
}

/// <summary>
/// Carries out a <see cref="RemovalPlan"/>: deletes services one at a time in the planned order,
/// drops each from the inventory as soon as it is gone, and journals every step. It stops at
/// the first failure, so nothing a failed service still needs is removed after it. Running it
/// again picks up where it stopped: the inventory only lists what is left.
/// </summary>
public static class ServiceRemover
{
    public static async Task<RemovalResult> RunAsync(RemovalRequest request, CancellationToken ct = default)
    {
        var (plan, lifecycle, config) = (request.Plan, request.Lifecycle, request.Config);

        void Report(RolloutProgressKind kind, string? service, string message)
            => request.OnProgress?.Invoke(new RolloutProgress(kind, service, message));

        await using var journal = await RolloutJournal.BeginAsync(
            request.State.StateDirectory, request.EnvironmentName, request.Target.DisplayName,
            plan.ToDelete.Count == 0 ? [] : [plan.ToDelete], ct: ct,
            operation: request.Operation, onSaved: request.State.JournalSink);

        if (plan.AlreadyGone.Count > 0)
        {
            await request.State.Inventory.UpdateAsync(inv => plan.AlreadyGone.ForEach(s => inv.Remove(s)), ct);
            await journal.LogAsync(null, $"dropped from state, already gone: {string.Join(", ", plan.AlreadyGone)}");
        }

        var deleted = new List<string>();
        string? failed = null, error = null;
        foreach (var service in plan.ToDelete)
        {
            var record = journal.Record.Service(service);
            try
            {
                ct.ThrowIfCancellationRequested();
                Report(RolloutProgressKind.Info, service, "deleting");
                await lifecycle.DeleteServiceAsync(config, service, request.Options, ct);
                await request.State.Inventory.UpdateAsync(inv => inv.Remove(service), ct);
                record.State = ServiceRolloutState.Deleted;
                deleted.Add(service);
                await journal.LogAsync(service, "deleted");
                Report(RolloutProgressKind.Success, service, "deleted");
            }
            catch (Exception ex)
            {
                failed = service;
                error = ex is OperationCanceledException ? "cancelled" : ex.Message;
                record.State = ServiceRolloutState.DeleteFailed;
                record.Detail = error;
                await journal.LogAsync(service, $"delete failed: {error}");
                Report(RolloutProgressKind.Error, service, $"delete failed: {error}");
                break;
            }
        }

        var extras = new List<string>();
        if (failed is null && request.WholeEnvironment)
        {
            try
            {
                extras.AddRange(await lifecycle.DestroyEnvironmentAsync(config, request.Options, ct));
                foreach (var e in extras)
                {
                    await journal.LogAsync(null, $"removed {e}");
                    Report(RolloutProgressKind.Success, null, $"removed {e}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = "(environment)";
                error = ex.Message;
                await journal.LogAsync(null, $"environment clean-up failed: {error}");
                Report(RolloutProgressKind.Error, null, $"environment clean-up failed: {error}");
            }
        }

        journal.Record.State = failed is null ? RolloutState.Succeeded : RolloutState.Failed;
        journal.Record.Reason = error;
        journal.Record.FinishedAt = DateTimeOffset.UtcNow;
        await journal.SaveAsync(CancellationToken.None);

        if (journal.SyncError is { } sync)
        {
            Report(RolloutProgressKind.Warning, null, $"Could not copy the journal to remote state: {sync}");
        }

        return new RemovalResult
        {
            Succeeded = failed is null,
            Deleted = deleted,
            FailedService = failed,
            Error = error,
            EnvironmentResourcesRemoved = extras,
            JournalPath = journal.Path,
            JournalId = journal.Record.Id
        };
    }
}
