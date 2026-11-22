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

    /// <summary>Called after every journal save, e.g. to copy it to remote state.</summary>
    public Func<RolloutRecord, CancellationToken, Task>? OnJournalSaved { get; init; }
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

        // Strategies are checked before the lock is taken or anything changes.
        var strategyErrors = await RolloutStrategyCheck.CheckAsync(target, manifest, config, healthGate: true, ct);
        if (strategyErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", strategyErrors));
        }

        var strategyMode = target.GetCapability<IRolloutStrategyTarget>()?.StrategyMode;
        var shifter = strategyMode == RolloutStrategyMode.Driven ? target.GetCapability<ITrafficShiftingTarget>() : null;

        var explanations = await target.ExplainAsync(manifest, config, ct);
        var waves = RolloutPlanner.Waves(
            explanations.Select(e => e.Name), await target.GetDependenciesAsync(manifest, config, ct));

        var warnings = new List<string>();
        var mutable = ImageTagCheck.MutableImages(explanations);
        if (mutable.Count > 0)
        {
            warnings.Add(
                $"{string.Join(", ", mutable)} use a mutable image tag (latest or none). A rollback re-deploys " +
                "the same reference, which may already point at the new image. Use a unique tag per release.");
        }

        await using var journal = await RolloutJournal.BeginAsync(
            request.StateDirectory, request.EnvironmentName, target.DisplayName, waves, ct: ct,
            onSaved: request.OnJournalSaved);
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
                var progressive = new List<(ServiceRolloutRecord Service, RolloutStrategy Strategy)>();
                foreach (var name in wave)
                {
                    var svc = record.Service(name);
                    var strategy = RolloutStrategies.For(config, name);
                    svc.Snapshot = await rollout.CaptureAsync(manifest, config, name, ct);
                    svc.State = ServiceRolloutState.Deploying;
                    touched.Add(svc);
                    await journal.LogAsync(name, svc.Snapshot.Existed
                        ? $"previous revision: {svc.Snapshot.Revision ?? "(unknown)"}"
                        : "new service");

                    if (strategy.IsProgressive)
                    {
                        svc.Strategy = strategy.Describe();
                        if (shifter is not null && svc.Snapshot.Existed)
                        {
                            // Run after this wave's other services are healthy, one at a time.
                            progressive.Add((svc, strategy));
                            continue;
                        }

                        if (shifter is not null)
                        {
                            var note = $"new service: deployed directly, {RolloutStrategyCheck.Name(strategy.Kind)} applies from its next deploy";
                            await journal.LogAsync(name, note);
                            Report(RolloutProgressKind.Info, name, note);
                        }
                    }

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
                    foreach (var (waiting, _) in progressive)
                    {
                        // Captured but never started: nothing changed, nothing to restore.
                        touched.Remove(waiting);
                        waiting.State = ServiceRolloutState.Pending;
                    }

                    break;
                }

                foreach (var svc in deployed)
                {
                    svc.State = ServiceRolloutState.Verifying;
                    await journal.SaveAsync();
                    Report(RolloutProgressKind.Info, svc.Name, "waiting for health");

                    // A strategy the platform runs itself takes its pauses on top of the health timeout.
                    var timeout = options.HealthTimeout;
                    if (strategyMode == RolloutStrategyMode.Native && RolloutStrategies.For(config, svc.Name) is { IsProgressive: true } native)
                    {
                        timeout += native.WaitTime;
                        Report(RolloutProgressKind.Info, svc.Name, $"{native.Describe()}, run by {target.DisplayName}");
                    }

                    string? lastDetail = null;
                    var gate = await HealthGate.WaitAsync(rollout, config, svc.Name, svc.Revision!,
                        timeout, options,
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

                foreach (var (svc, strategy) in progressive)
                {
                    if (failure is not null)
                    {
                        // Captured but never started: nothing changed, nothing to restore.
                        touched.Remove(svc);
                        svc.State = ServiceRolloutState.Pending;
                        continue;
                    }

                    failure = await ProgressAsync(svc, strategy);
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

        async Task<string?> ProgressAsync(ServiceRolloutRecord svc, RolloutStrategy strategy)
        {
            var name = svc.Name;
            var kind = RolloutStrategyCheck.Name(strategy.Kind);
            Report(RolloutProgressKind.Info, name, $"{strategy.Describe()}: starting the new version next to {svc.Snapshot!.Revision}");

            var started = await shifter!.StartCandidateAsync(manifest, config, request.OutputDirectory, name, strategy, svc.Snapshot, ct);
            if (!started.Success || started.Revision is null)
            {
                return await FailAsync(svc, $"{kind} candidate failed to start: {started.Error ?? "the target reported no revision"}");
            }

            svc.Candidate = started.Revision;
            svc.Revision = started.Revision;
            svc.PreviewUrl = started.PreviewUrl;
            svc.TrafficPercent = 0;
            await journal.LogAsync(name, $"candidate {started.Revision} started" +
                                         (started.PreviewUrl is { } url ? $", preview at {url}" : ""));
            Report(RolloutProgressKind.Info, name, $"candidate {started.Revision} started");
            if (started.PreviewUrl is { } preview)
            {
                Report(RolloutProgressKind.Info, name, $"preview: {preview}");
            }

            svc.State = ServiceRolloutState.Verifying;
            if (await GateCandidateAsync(svc, "waiting for the candidate to become healthy") is { } notHealthy)
            {
                return notHealthy;
            }

            if (strategy.Kind == RolloutStrategyKind.Canary)
            {
                for (var i = 0; i < strategy.Steps.Count; i++)
                {
                    var step = strategy.Steps[i];
                    var share = shifter.Precision == TrafficPrecision.Fixed && i > 0 && svc.TrafficPercent is { } fixedShare
                        ? fixedShare
                        : await shifter.ShiftTrafficAsync(config, name, started.Revision, step.Weight, ct);
                    svc.TrafficPercent = share;
                    var message = $"step {i + 1}/{strategy.Steps.Count}: {share}% of the traffic" +
                                  (share != step.Weight ? $" (asked for {step.Weight}%)" : "") +
                                  $" for {Durations.Format(step.Pause)}";
                    await journal.LogAsync(name, message);
                    Report(RolloutProgressKind.Info, name, message);

                    if ((await GateCandidateAsync(svc, null) ?? await WatchCandidateAsync(svc, step.Pause)) is { } failed)
                    {
                        return failed;
                    }
                }
            }
            else
            {
                if (strategy.PreviewPause > TimeSpan.Zero)
                {
                    var message = $"previewing for {Durations.Format(strategy.PreviewPause)} before the switch";
                    await journal.LogAsync(name, message);
                    Report(RolloutProgressKind.Info, name, message);
                    if (await WatchCandidateAsync(svc, strategy.PreviewPause) is { } failed)
                    {
                        return failed;
                    }
                }

                svc.TrafficPercent = await shifter.ShiftTrafficAsync(config, name, started.Revision, 100, ct);
                var switched = $"switched {svc.TrafficPercent}% of the traffic to the candidate; keeping the old version for " +
                               Durations.Format(strategy.ScaleDownDelay);
                await journal.LogAsync(name, switched);
                Report(RolloutProgressKind.Info, name, switched);
                if ((await GateCandidateAsync(svc, null) ?? await WatchCandidateAsync(svc, strategy.ScaleDownDelay)) is { } failed2)
                {
                    return failed2;
                }
            }

            Report(RolloutProgressKind.Info, name, "promoting the candidate");
            var promoted = await shifter.PromoteAsync(manifest, config, request.OutputDirectory, name, started.Revision, ct);
            await journal.LogAsync(name, $"promoted {started.Revision}" + (promoted is not null && promoted != started.Revision ? $" as {promoted}" : ""));
            if (promoted is not null)
            {
                svc.Revision = promoted;
                var gate = await HealthGate.WaitAsync(rollout, config, name, promoted, options.HealthTimeout, options, ct: ct);
                if (gate.Outcome != GateOutcome.Healthy)
                {
                    return await FailAsync(svc, $"promoted revision {promoted} did not become healthy: {gate.Detail}");
                }
            }

            svc.TrafficPercent = 100;
            svc.State = ServiceRolloutState.Healthy;
            svc.Detail = null;
            await journal.LogAsync(name, "healthy");
            Report(RolloutProgressKind.Success, name, $"healthy ({kind} complete)");

            try
            {
                await shifter.FinishAsync(config, name, started.Revision, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var warning = $"{name}: clean-up after the {kind} rollout failed: {ex.Message}";
                warnings.Add(warning);
                Report(RolloutProgressKind.Warning, name, warning);
                await journal.LogAsync(name, warning);
            }

            return null;
        }

        async Task<string?> GateCandidateAsync(ServiceRolloutRecord svc, string? waiting)
        {
            await journal.SaveAsync();
            if (waiting is not null)
            {
                Report(RolloutProgressKind.Info, svc.Name, waiting);
            }

            string? lastDetail = null;
            var gate = await HealthGate.WaitAsync(
                probe => shifter!.ObserveCandidateAsync(config, svc.Name, svc.Candidate!, probe),
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
            return gate.Outcome == GateOutcome.Healthy
                ? null
                : await FailAsync(svc, gate.Outcome == GateOutcome.TimedOut
                    ? $"candidate {gate.Detail}"
                    : $"candidate unhealthy: {gate.Detail}");
        }

        async Task<string?> WatchCandidateAsync(ServiceRolloutRecord svc, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return null;
            }

            var watch = await HealthGate.WatchAsync(
                probe => shifter!.ObserveCandidateAsync(config, svc.Name, svc.Candidate!, probe), duration, options, ct: ct);
            return watch.Outcome == GateOutcome.Healthy
                ? null
                : await FailAsync(svc, $"candidate unhealthy at {svc.TrafficPercent ?? 0}% of the traffic: {watch.Detail}");
        }

        async Task<string> FailAsync(ServiceRolloutRecord svc, string detail)
        {
            svc.State = ServiceRolloutState.Failed;
            svc.Detail = detail;
            var message = $"{svc.Name}: {detail}";
            await journal.LogAsync(svc.Name, message);
            Report(RolloutProgressKind.Error, svc.Name, detail);
            return message;
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
        if (journal.SyncError is { } sync)
        {
            warnings = [.. warnings, $"Could not copy the rollout journal to remote state: {sync}"];
        }

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
