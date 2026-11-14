using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Lifecycle;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;

namespace Deployify.Core.Deployments;

/// <summary>How <see cref="DeploymentRunner.RunAsync"/> deploys.</summary>
public sealed class DeploymentRunOptions
{
    /// <summary>Build and push project images with Deployify's own publisher before deploying.</summary>
    public bool Publish { get; init; } = true;

    /// <summary>Use a health-gated rollout when the target supports one (as <c>deployify apply</c> does).</summary>
    public bool HealthGate { get; init; } = true;

    public RolloutOptions Rollout { get; init; } = new();

    /// <summary>
    /// Stop when the manifest has resources Deployify won't deploy, even if nothing depends on
    /// them. Without it, only resources a deployed service depends on stop the run.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Asked once the plan is known, before anything changes. Returning false stops the run
    /// without deploying. Null deploys without asking.
    /// </summary>
    public Func<PlanResult, CancellationToken, Task<bool>>? Approve { get; init; }
}

/// <summary>What a deployment run did.</summary>
public sealed class DeploymentRunResult
{
    public required DeploymentRunOutcome Outcome { get; init; }

    /// <summary>One line describing the outcome.</summary>
    public required string Summary { get; init; }

    public PlanResult? Plan { get; init; }

    /// <summary>Per-service results of a plain apply; empty after a rollout.</summary>
    public IReadOnlyList<DeploymentResult> Results { get; init; } = [];

    /// <summary>The rollout result when the run used a health-gated rollout.</summary>
    public RolloutResult? Rollout { get; init; }

    public bool Succeeded => Outcome is DeploymentRunOutcome.Deployed or DeploymentRunOutcome.Declined;
}

public enum DeploymentRunOutcome
{
    Deployed,

    /// <summary>The approval was declined; nothing changed.</summary>
    Declined,

    Failed
}

/// <summary>
/// The deploy sequence for one environment: plan, approve, publish, record in state, store
/// generated secrets, then apply (health-gated when the target supports it). Used by the Aspire
/// integration; the CLI's apply and ci commands run the same steps with their own console output.
/// </summary>
public sealed class DeploymentRunner(DeploymentSession session)
{
    private IDeploymentReporter Reporter => session.Reporter;

    public async Task<DeploymentRunResult> RunAsync(
        AspireManifest manifest, DeploymentRunOptions options, CancellationToken ct = default)
    {
        var target = session.Target;
        var config = session.Config;

        // ── Manifest check ─────────────────────────────────────────────────────
        // LoadManifestAsync already reported each resource that won't be deployed.
        if (session.ManifestAudit.Blocks(options.Strict))
        {
            var summary = session.ManifestAudit.BlockedSummary(options.Strict);
            Reporter.Error(summary);
            return Failed(summary);
        }

        // ── Plan ───────────────────────────────────────────────────────────────
        PlanResult plan;
        await using (var stage = await Reporter.BeginStageAsync($"Planning changes on {target.DisplayName}", ct))
        {
            try
            {
                plan = session.WithSkipped(await target.PlanAsync(manifest, config, session.OutputDirectory, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stage.CompleteAsync($"Plan failed: {ex.Message}", DeploymentStageOutcome.Failed, ct);
                return Failed($"Plan failed: {ex.Message}");
            }

            foreach (var u in plan.Unknown)
            {
                Reporter.Warn($"{u.ServiceName}: could not check ({u.Reason})");
            }

            if (plan.DeleteCount > 0 && session.TargetType != DeploymentTargetType.Compose)
            {
                Reporter.Info($"{plan.DeleteCount} service(s) no longer in the manifest ({string.Join(", ", plan.ToDelete)}) " +
                              "stay in place; 'deployify apply --prune' removes them.");
            }

            await stage.CompleteAsync(Describe(plan),
                plan.UnknownCount > 0 || plan.Skipped.Count > 0 ? DeploymentStageOutcome.Warning : DeploymentStageOutcome.Succeeded, ct);
        }

        // ── Approve ────────────────────────────────────────────────────────────
        if (options.Approve is not null && !await options.Approve(plan, ct))
        {
            Reporter.Warn($"Deployment to {session.EnvName} was not approved; nothing changed.");
            return new DeploymentRunResult
            {
                Outcome = DeploymentRunOutcome.Declined,
                Summary = "Not approved; nothing changed.",
                Plan = plan
            };
        }

        // ── Publish ────────────────────────────────────────────────────────────
        if (options.Publish && manifest.Resources.Values.Any(r => r.IsBuiltImage))
        {
            await using var stage = await Reporter.BeginStageAsync(
                $"Publishing images (tag {config.ImageTag}, {config.ContainerRuntime})", ct);
            try
            {
                await target.PublishAsync(manifest, config, session.AppHostDirectory, ct);
                await session.RecordPublishedImagesAsync(manifest, ct);
                await stage.CompleteAsync(config.PublishedImages.HasChanges
                    ? $"Images published; {config.PublishedImages.Recorded.Count} pinned by digest."
                    : "Images published.", ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stage.CompleteAsync($"Publish failed: {ex.Message}", DeploymentStageOutcome.Failed, ct);
                return Failed($"Publish failed: {ex.Message}", plan);
            }
        }

        // ── Record + secrets ───────────────────────────────────────────────────
        try
        {
            await InventoryRecorder.RecordDesiredAsync(session.State, target, manifest, config, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Could not record the deployment in state ({session.State.Backend.Description}): {ex.Message}";
            Reporter.Error(message);
            return Failed(message, plan);
        }

        try
        {
            await session.SaveGeneratedSecretsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Reporter.Error(ex.Message);
            return Failed(ex.Message, plan);
        }

        // ── Apply ──────────────────────────────────────────────────────────────
        var result = await ResolveRolloutAsync(options.HealthGate, ct) is { } rollout
            ? await RolloutAsync(rollout, manifest, plan, options.Rollout, ct)
            : await ApplyAsync(manifest, plan, ct);

        if (result.Succeeded && config.Prune)
        {
            Reporter.Warn("This environment has prune enabled, but services are not pruned from 'aspire deploy' yet. " +
                          "Run 'deployify apply --prune' to remove services that left the manifest.");
        }

        return result;
    }

    /// <summary>
    /// The target's health-gated rollout, or null when it has none or can't use it right now
    /// (said why through the reporter).
    /// </summary>
    public async Task<IRolloutTarget?> ResolveRolloutAsync(bool healthGate, CancellationToken ct = default)
    {
        if (!healthGate)
        {
            return null;
        }

        if (session.Target.GetCapability<IRolloutTarget>() is not { } rollout)
        {
            Reporter.Info($"{session.Target.DisplayName} has no health-gated rollout yet: deploying without health checks or automatic rollback.");
            return null;
        }

        if (await rollout.GetUnavailableReasonAsync(session.Config, ct) is { } reason)
        {
            Reporter.Warn($"No health-gated rollout: {reason}. Deploying without health checks or automatic rollback.");
            return null;
        }

        return rollout;
    }

    private async Task<DeploymentRunResult> RolloutAsync(
        IRolloutTarget rollout, AspireManifest manifest, PlanResult plan, RolloutOptions options, CancellationToken ct)
    {
        await using var stage = await Reporter.BeginStageAsync($"Rolling out to {session.EnvName} (health-gated)", ct);
        var request = new RolloutRequest
        {
            Target = session.Target,
            Rollout = rollout,
            Manifest = manifest,
            Config = session.Config,
            EnvironmentName = session.EnvName,
            OutputDirectory = session.OutputDirectory,
            StateDirectory = session.Directory,
            Options = options,
            OnProgress = p => Report(p),
            OnJournalSaved = session.State.JournalSink
        };

        RolloutResult result;
        try
        {
            result = await new RolloutEngine().RunAsync(request, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Raised before anything changed, e.g. another rollout holds the lock.
            await stage.CompleteAsync(ex.Message, DeploymentStageOutcome.Failed, ct);
            return Failed(ex.Message, plan);
        }
        finally
        {
            // Not cancellable: a cancelled rollout has still changed (and maybe restored) secrets.
            await SaveSecretReferencesAsync(CancellationToken.None);
        }

        foreach (var w in result.Warnings)
        {
            Reporter.Warn(w);
        }

        var summary = result.Outcome switch
        {
            RolloutOutcome.Succeeded => $"Rollout complete: {result.Services.Count} service(s) healthy in {session.EnvName}.",
            RolloutOutcome.RolledBack => $"Rollout failed and was rolled back. {result.Reason}",
            RolloutOutcome.RollbackFailed =>
                "Rollout failed and the rollback did not complete. Check these services by hand: " +
                string.Join(", ", result.Services.Where(s => s.State == ServiceRolloutState.RollbackFailed).Select(s => s.Name)) +
                $". Cause: {result.Reason}",
            _ => $"Rollout failed. {result.Reason}"
        };
        if (!string.IsNullOrEmpty(result.JournalPath))
        {
            Reporter.Info($"Rollout journal: {result.JournalPath}");
        }

        var succeeded = result.Outcome == RolloutOutcome.Succeeded;
        await stage.CompleteAsync(summary, succeeded ? DeploymentStageOutcome.Succeeded : DeploymentStageOutcome.Failed, ct);
        return new DeploymentRunResult
        {
            Outcome = succeeded ? DeploymentRunOutcome.Deployed : DeploymentRunOutcome.Failed,
            Summary = summary.Trim(),
            Plan = plan,
            Rollout = result
        };
    }

    private async Task<DeploymentRunResult> ApplyAsync(AspireManifest manifest, PlanResult plan, CancellationToken ct)
    {
        await using var stage = await Reporter.BeginStageAsync($"Applying to {session.EnvName}", ct);
        IReadOnlyList<DeploymentResult> results;
        try
        {
            results = await session.Target.ApplyAsync(manifest, session.Config, session.OutputDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveSecretReferencesAsync(CancellationToken.None);
            await stage.CompleteAsync($"Apply failed: {ex.Message}", DeploymentStageOutcome.Failed, ct);
            return Failed($"Apply failed: {ex.Message}", plan);
        }

        await SaveSecretReferencesAsync(ct);

        foreach (var r in results)
        {
            if (r.Success)
            {
                Reporter.Info($"✓ {r.ServiceName}" + (r.DeploymentId is not null ? $" ({r.DeploymentId})" : ""));
            }
            else
            {
                Reporter.Error($"✗ {r.ServiceName}: {r.Error}");
            }
        }

        var failed = results.Count(r => !r.Success);
        var succeeded = results.Count - failed;
        var summary = failed == 0
            ? $"Apply complete: {succeeded} service(s) deployed to {session.EnvName}."
            : $"Apply finished with errors: {succeeded} succeeded, {failed} failed.";
        await stage.CompleteAsync(summary, failed == 0 ? DeploymentStageOutcome.Succeeded : DeploymentStageOutcome.Failed, ct);
        return new DeploymentRunResult
        {
            Outcome = failed == 0 ? DeploymentRunOutcome.Deployed : DeploymentRunOutcome.Failed,
            Summary = summary,
            Plan = plan,
            Results = results
        };
    }

    /// <summary>Records where apply wrote each secret; a failure is only a warning.</summary>
    private async Task SaveSecretReferencesAsync(CancellationToken ct)
    {
        try
        {
            await session.SaveSecretReferencesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Reporter.Warn($"Could not record secret references: {ex.Message}");
        }
    }

    private void Report(RolloutProgress p)
    {
        var message = p.Service is null ? p.Message : $"{p.Service}: {p.Message}";
        switch (p.Kind)
        {
            case RolloutProgressKind.Error:
                Reporter.Error(message);
                break;
            case RolloutProgressKind.Warning:
                Reporter.Warn(message);
                break;
            default:
                Reporter.Info(message);
                break;
        }
    }

    /// <summary>"+1 create / ~2 update / -0 delete / 3 unchanged", with unchecked services when any.</summary>
    public static string Describe(PlanResult plan)
        => $"+{plan.CreateCount} create / ~{plan.UpdateCount} update / -{plan.DeleteCount} delete / {plan.Unchanged.Count} unchanged" +
           (plan.UnknownCount > 0 ? $" / ?{plan.UnknownCount} unchecked" : "") +
           (plan.Skipped.Count > 0 ? $" / {plan.Skipped.Count} not deployed ({string.Join(", ", plan.Skipped.Select(s => s.Name))})" : "");

    private static DeploymentRunResult Failed(string summary, PlanResult? plan = null)
        => new() { Outcome = DeploymentRunOutcome.Failed, Summary = summary, Plan = plan };
}
