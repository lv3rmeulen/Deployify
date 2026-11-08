using System;
using System.CommandLine;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Lifecycle;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Console glue for deployment state shared by apply, ci, destroy and state: recording what is
/// deployed, pruning, and printing removal plans.
/// </summary>
internal static class StateCli
{
    /// <summary>Exit code when the deploy worked but the prune was refused or failed.</summary>
    public const int PruneFailedExitCode = 4;

    public static readonly Option<bool> Prune = new("--prune")
    {
        Description = "After a successful deploy, remove services that are no longer in the manifest " +
                      "(only ones Deployify deployed and still marks as its own).",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> ForcePrune = new("--force-prune")
    {
        Description = "Prune even when it would remove more than half of the services, or all of them.",
        DefaultValueFactory = _ => false
    };

    public static void AddPruneOptions(Command cmd)
    {
        cmd.Options.Add(Prune);
        cmd.Options.Add(ForcePrune);
    }

    public static void Warn(string message) => AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {message}");

    public static bool PruneRequested(ParseResult parseResult, EnvironmentConfig config)
        => parseResult.GetValue(Prune) || config.Prune;

    /// <summary>
    /// Records the manifest's services in the inventory before deploying. Prints and returns
    /// false when state can't be written, in which case nothing should be deployed.
    /// </summary>
    public static async Task<bool> RecordDeploymentAsync(ResolvedContext resolved, AspireManifest manifest, CancellationToken ct)
    {
        try
        {
            await InventoryRecorder.RecordDesiredAsync(resolved.State, resolved.Target, manifest, resolved.Config, ct: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not record the deployment in state ({resolved.State.Backend.Description}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The services that plan found on the target but not in the manifest, and that only apply
    /// --prune removes. Says so, so the plan's "to delete" count doesn't read as a promise.
    /// </summary>
    public static void PrintLeftInPlace(PlanResult plan, DeploymentTargetType target, bool pruning)
    {
        // Compose removes a project's orphaned containers on every 'up' anyway.
        if (pruning || plan.DeleteCount == 0 || target == DeploymentTargetType.Compose)
        {
            return;
        }

        var names = string.Join(", ", plan.ToDelete);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{plan.DeleteCount} service(s) no longer in the manifest ({names}) stay in place; apply --prune removes them.[/]");
    }

    /// <summary>
    /// Removes the services that left the manifest, after a successful deploy. Returns 0, or
    /// <see cref="PruneFailedExitCode"/> when the prune was refused or didn't finish.
    /// </summary>
    public static async Task<int> PruneAsync(
        ResolvedContext resolved, AspireManifest manifest, PlanResult? plan, bool force, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("\n[bold]Prune[/]");
        if (resolved.Target is not IResourceLifecycle lifecycle)
        {
            Warn($"{resolved.Target.DisplayName} can't prune yet; nothing was removed.");
            return PruneFailedExitCode;
        }

        if (plan is { UnknownCount: > 0 })
        {
            Warn($"Not pruning: plan could not check {string.Join(", ", plan.UnknownNames)}, so it can't be sure what is unused.");
            return PruneFailedExitCode;
        }

        RemovalPlan removal;
        try
        {
            var desired = resolved.Target.Explain(manifest, resolved.Config).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var inventory = await resolved.State.Inventory.LoadAsync(ct);
            var managed = await lifecycle.ListManagedAsync(resolved.Config, ct);
            var candidates = inventory.Services.Select(s => s.Name).Concat(managed.Select(m => m.Name))
                .Where(s => !desired.Contains(s));
            removal = RemovalPlanner.Build(candidates, inventory, managed, resolved.EnvName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never prune from a guess: an unreachable platform looks like an empty one.
            Warn($"Not pruning: could not list what Deployify manages on {resolved.Target.DisplayName}: {ex.Message}");
            return PruneFailedExitCode;
        }

        PrintRemovalPlan(removal, "remove");
        if (removal.IsEmpty)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to prune.[/]");
            return 0;
        }

        if (RemovalPlanner.PruneSafetyCheck(removal) is { } reason && !force)
        {
            Warn($"Not pruning: {reason}. Check the manifest, then pass --force-prune if that is really intended.");
            return PruneFailedExitCode;
        }

        var result = await ServiceRemover.RunAsync(new RemovalRequest
        {
            Target = resolved.Target,
            Lifecycle = lifecycle,
            Config = resolved.Config,
            EnvironmentName = resolved.EnvName,
            State = resolved.State,
            Plan = removal,
            Operation = RolloutOperation.Prune,
            OnProgress = RolloutCli.Print
        }, ct);

        if (result.Succeeded)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Pruned[/] {result.Deleted.Count} service(s).");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[red]Prune stopped at {result.FailedService}:[/] {result.Error ?? ""} Run apply --prune again to continue.");
        return PruneFailedExitCode;
    }

    /// <summary>Prints what a removal will and won't touch. <paramref name="verb"/>: "destroy" or "remove".</summary>
    public static void PrintRemovalPlan(RemovalPlan plan, string verb)
    {
        foreach (var s in plan.ToDelete)
        {
            AnsiConsole.MarkupLineInterpolated($"  [red]- {s}[/]");
        }

        foreach (var s in plan.AlreadyGone)
        {
            AnsiConsole.MarkupLineInterpolated($"  [dim]· {s}: already gone, only dropped from state[/]");
        }

        foreach (var s in plan.NotInState)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"  [yellow]? {s}: marked as Deployify's but not in this environment's state; left alone ('deployify state adopt' to include it)[/]");
        }

        foreach (var s in plan.OtherEnvironment)
        {
            AnsiConsole.MarkupLineInterpolated($"  [yellow]? {s}: tagged for another environment; left alone[/]");
        }

        if (plan.ToDelete.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]{plan.ToDelete.Count} service(s) to {verb}.[/]");
        }
    }

    /// <summary>Prints a lock holder, or that the lock is free.</summary>
    public static void PrintLock(LockInfo? holder)
    {
        if (holder is null)
        {
            AnsiConsole.MarkupLine("Lock: [green]free[/]");
            return;
        }

        AnsiConsole.MarkupLineInterpolated($"Lock: [yellow]held[/] by {holder.Describe(DateTimeOffset.UtcNow)}");
        AnsiConsole.MarkupLineInterpolated($"[dim]      expires {holder.ExpiresAt.ToLocalTime():G} unless renewed[/]");
    }

    public static IReadOnlyList<string> SplitList(IEnumerable<string>? values)
        => [.. (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
}
