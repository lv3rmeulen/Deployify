using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Rollouts;

/// <summary>Everything one rollout needs.</summary>
public sealed class RolloutRequest
{
    public required IDeploymentTarget Target { get; init; }
    public required IRolloutTarget Rollout { get; init; }
    public required AspireManifest Manifest { get; init; }
    public required EnvironmentConfig Config { get; init; }
    public required string EnvironmentName { get; init; }

    /// <summary>Where plan wrote the artifacts.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Directory that holds deployify-state.json; the journal goes under it.</summary>
    public required string StateDirectory { get; init; }

    public RolloutOptions Options { get; init; } = new();
    public Action<RolloutProgress>? OnProgress { get; init; }
}

/// <summary>
/// Deploys services wave by wave in dependency order, gates each wave on health, and on any
/// failure, timeout or cancellation restores every touched service in reverse order.
/// </summary>
public sealed class RolloutEngine
{
    public async Task<RolloutResult> RunAsync(RolloutRequest request, CancellationToken ct = default)
    {
        var (target, rollout, manifest, config, options) =
            (request.Target, request.Rollout, request.Manifest, request.Config, request.Options);

        var explanations = target.Explain(manifest, config);
        var waves = RolloutPlanner.Waves(explanations.Select(e => e.Name), target.GetDependencies(manifest, config));

        var warnings = new List<string>();
        var mutable = ImageTagCheck.MutableImages(explanations);
        if (mutable.Count > 0)
        {
            warnings.Add(
                $"{string.Join(", ", mutable)} use a mutable image tag (latest or none). A rollback re-deploys " +
                "the same reference, which may already point at the new image. Use a unique tag per release.");
        }

        await using var journal = await RolloutJournal.BeginAsync(
            request.StateDirectory, request.EnvironmentName, target.DisplayName, waves, ct: ct);
        var record = journal.Record;

        void Report(RolloutProgressKind kind, string? service, string message)
            => request.OnProgress?.Invoke(new RolloutProgress(kind, service, message));

        foreach (var w in warnings)
        {
            Report(RolloutProgressKind.Warning, null, w);
            await journal.LogAsync(null, w);
        }

        // Services in the order they were touched; the rollback walks this backwards.
        var touched = new List<ServiceRolloutRecord>();
        string? failure = null;

        try
        {
            for (var i = 0; i < waves.Count && failure is null; i++)
            {
                var wave = waves[i];
                Report(RolloutProgressKind.Info, null, $"Wave {i + 1}/{waves.Count}: {string.Join(", ", wave)}");

                var deployed = new List<ServiceRolloutRecord>();
                foreach (var name in wave)
                {
                    var svc = record.Service(name);
                    svc.Snapshot = await rollout.CaptureAsync(manifest, config, name, ct);
                    svc.State = ServiceRolloutState.Deploying;
                    touched.Add(svc);
                    await journal.LogAsync(name, svc.Snapshot.Existed
                        ? $"previous revision: {svc.Snapshot.Revision ?? "(unknown)"}"
                        : "new service");

                    var applied = await rollout.ApplyServiceAsync(manifest, config, request.OutputDirectory, name, ct);
                    svc.Revision = applied.Revision;
                    if (!applied.Success || applied.Revision is null)
                    {
                        svc.State = ServiceRolloutState.Failed;
                        svc.Detail = applied.Error ?? "the target reported no revision";
                        failure = $"{name}: deploy failed: {svc.Detail}";
                        await journal.LogAsync(name, failure);
                        Report(RolloutProgressKind.Error, name, $"deploy failed: {svc.Detail}");
                        break;
                    }

                    await journal.LogAsync(name, $"deployed revision {applied.Revision}");
                    Report(RolloutProgressKind.Info, name, $"deployed {applied.Revision}");
                    deployed.Add(svc);
                }

                if (failure is not null)
                {
                    break;
                }

                foreach (var svc in deployed)
                {
                    svc.State = ServiceRolloutState.Verifying;
                    await journal.SaveAsync();
                    Report(RolloutProgressKind.Info, svc.Name, "waiting for health");

                    string? lastDetail = null;
                    var gate = await HealthGate.WaitAsync(rollout, config, svc.Name, svc.Revision!,
                        options.HealthTimeout, options,
                        obs =>
                        {
                            if (obs.Detail is { } d && d != lastDetail)
                            {
                                lastDetail = d;
                                Report(RolloutProgressKind.Info, svc.Name, d);
                            }
                        },
                        ct);

                    svc.Detail = gate.Detail;
                    switch (gate.Outcome)
                    {
                        case GateOutcome.Healthy:
                            svc.State = ServiceRolloutState.Healthy;
                            await journal.LogAsync(svc.Name, "healthy");
                            Report(RolloutProgressKind.Success, svc.Name, "healthy");
                            continue;
                        case GateOutcome.RevertedByPlatform:
                            svc.State = ServiceRolloutState.RevertedByPlatform;
                            svc.RestoredRevision = gate.Revision;
                            failure = $"{svc.Name}: reverted by the platform: {gate.Detail}";
                            break;
                        case GateOutcome.TimedOut:
                            svc.State = ServiceRolloutState.Failed;
                            failure = $"{svc.Name}: {gate.Detail}";
                            break;
                        default:
                            svc.State = ServiceRolloutState.Failed;
                            failure = $"{svc.Name}: unhealthy: {gate.Detail}";
                            break;
                    }

                    await journal.LogAsync(svc.Name, failure);
                    Report(RolloutProgressKind.Error, svc.Name, failure);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            failure = "cancelled";
            Report(RolloutProgressKind.Error, null, "Rollout cancelled.");
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            Report(RolloutProgressKind.Error, null, ex.Message);
        }

        if (failure is null)
        {
            try
            {
                await rollout.CompleteAsync(manifest, config, request.OutputDirectory, ct);
            }
            catch (Exception ex)
            {
                var warning = $"Clean-up after the rollout failed: {ex.Message}";
                warnings.Add(warning);
                Report(RolloutProgressKind.Warning, null, warning);
                await journal.LogAsync(null, warning);
            }

            return await FinishAsync(journal, RolloutState.Succeeded, RolloutOutcome.Succeeded, null, warnings);
        }

        // Services still marked deploying or verifying when the rollout stopped are failed too.
        foreach (var svc in touched.Where(s => s.State is ServiceRolloutState.Deploying or ServiceRolloutState.Verifying))
        {
            svc.State = ServiceRolloutState.Failed;
            svc.Detail ??= failure;
        }

        if (touched.Count == 0 || !options.AutoRollback)
        {
            if (touched.Count > 0)
            {
                Report(RolloutProgressKind.Warning, null, "Automatic rollback is off; the failed revision is left in place.");
            }

            return await FinishAsync(journal, RolloutState.Failed, RolloutOutcome.Failed, failure, warnings);
        }

        var rolledBack = await RollBackAsync(rollout, config, options, journal, touched, Report);
        return rolledBack
            ? await FinishAsync(journal, RolloutState.RolledBack, RolloutOutcome.RolledBack, failure, warnings)
            : await FinishAsync(journal, RolloutState.RollbackFailed, RolloutOutcome.RollbackFailed, failure, warnings);
    }

    /// <summary>
    /// Restores <paramref name="touched"/> in reverse order on a token of its own, so the
    /// cancellation that stopped the rollout cannot stop the rollback. Returns true when every
    /// service is back and, where it existed before, healthy again. Also used by
    /// <c>deployify rollback</c> to undo an earlier rollout from its journal.
    /// </summary>
    public static async Task<bool> RollBackAsync(
        IRolloutTarget rollout, EnvironmentConfig config, RolloutOptions options, RolloutJournal journal,
        IReadOnlyList<ServiceRolloutRecord> touched, Action<RolloutProgressKind, string?, string> report)
    {
        journal.Record.State = RolloutState.RollingBack;
        await journal.LogAsync(null, "rolling back");
        report(RolloutProgressKind.Warning, null, $"Rolling back {touched.Count} service(s)…");

        using var budget = new CancellationTokenSource(options.RollbackTimeout);
        var allOk = true;

        foreach (var svc in touched.Reverse())
        {
            var snapshot = svc.Snapshot!;
            try
            {
                var byPlatform = svc.State == ServiceRolloutState.RevertedByPlatform;
                var waitFor = byPlatform
                    ? svc.RestoredRevision ?? (snapshot.Existed ? snapshot.Revision : null)
                    : await rollout.RestoreAsync(config, snapshot, budget.Token);
                svc.RestoredRevision = waitFor;
                await journal.SaveAsync();

                if (waitFor is not null)
                {
                    var gate = await HealthGate.WaitAsync(rollout, config, svc.Name, waitFor,
                        options.HealthTimeout, options, ct: budget.Token);
                    if (gate.Outcome != GateOutcome.Healthy)
                    {
                        throw new InvalidOperationException(
                            $"restored revision {waitFor} did not become healthy: {gate.Detail}");
                    }
                }

                svc.State = byPlatform ? ServiceRolloutState.RevertedByPlatform : ServiceRolloutState.RolledBack;
                var message = waitFor is null ? "new service taken out of service"
                    : byPlatform ? $"platform reverted to {waitFor}, healthy"
                    : $"restored {waitFor}, healthy";
                await journal.LogAsync(svc.Name, message);
                report(RolloutProgressKind.Success, svc.Name, message);
            }
            catch (Exception ex)
            {
                allOk = false;
                svc.State = ServiceRolloutState.RollbackFailed;
                svc.Detail = budget.IsCancellationRequested
                    ? $"rollback ran out of time ({HealthGate.Format(options.RollbackTimeout)})"
                    : ex.Message;
                await journal.LogAsync(svc.Name, $"rollback failed: {svc.Detail}");
                report(RolloutProgressKind.Error, svc.Name, $"rollback failed: {svc.Detail}");
            }
        }

        return allOk;
    }

    private static async Task<RolloutResult> FinishAsync(
        RolloutJournal journal, RolloutState state, RolloutOutcome outcome, string? reason, IReadOnlyList<string> warnings)
    {
        journal.Record.State = state;
        journal.Record.Reason = reason;
        journal.Record.FinishedAt = DateTimeOffset.UtcNow;
        await journal.SaveAsync();
        return new RolloutResult
        {
            Outcome = outcome,
            Reason = reason,
            RolloutId = journal.Record.Id,
            JournalPath = journal.Path,
            Services = journal.Record.Services,
            Warnings = warnings
        };
    }
}
