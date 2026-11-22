using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using Deployify.Commands.Json;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Spectre.Console;

namespace Deployify.Commands;

public static class PlanCommand
{
    public static Command Build()
    {
        var cmd = new Command("plan", "Preview changes that would be applied to the target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(GlobalOptions.Strict);

        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "plan", async json =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var strict = parseResult.GetValue(GlobalOptions.Strict);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            json?.Use(resolved);
            var (_, config, envName, targetType, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            AnsiConsole.MarkupLine("[bold]Deployify Plan[/]\n");

            // PlanAsync writes artifacts to disk first, then diffs against the live cluster.
            // Targets treat an unreachable cluster as "everything is new" and log a
            // warning, so an exception here means planning itself failed.
            PlanResult plan;
            try
            {
                plan = resolved.WithSkipped(await target.PlanAsync(manifest, config, outputDir, ct));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error($"Plan failed: {ex.Message}");
                return 1;
            }

            PrintSection(plan.ToCreate, "[green]+ Create[/]", Color.Green);
            PrintSection(plan.ToUpdate, "[yellow]~ Update[/]", Color.Yellow);
            PrintSection(plan.ToDelete, "[red]- Delete[/]", Color.Red);
            PlanOutput.PrintUnknown(plan);

            AnsiConsole.MarkupLine(PlanOutput.Summary(plan));
            StateCli.PrintLeftInPlace(plan, targetType, pruning: config.Prune);

            // Only when every service is known to match: an unchecked one isn't "up to date".
            if (plan.IsUpToDate)
            {
                AnsiConsole.MarkupLine("[dim]No changes. Everything is up to date.[/]");
            }

            await RolloutCli.PrintStrategiesAsync(target, manifest, config, ct);

            // Plan is the pre-flight check: fail when apply would refuse.
            var blocked = PlanOutput.AuditBlocks(resolved, strict);
            var strategyErrors = await RolloutStrategyCheck.CheckAsync(target, manifest, config, healthGate: true, ct);
            foreach (var error in strategyErrors)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {error}");
            }

            if (json is not null)
            {
                json.Result = ToJson(plan, resolved, strict, blocked, strategyErrors);
            }

            blocked |= strategyErrors.Count > 0;

            return blocked ? 1 : 0;
        }));

        return cmd;
    }

    /// <summary>The <c>result</c> of <c>plan --output json</c>.</summary>
    internal static JsonPlan ToJson(
        PlanResult plan, ResolvedContext resolved, bool strict, bool blocked, IReadOnlyList<string>? strategyErrors = null) => new(
        Create: plan.ToCreate,
        Update: plan.ToUpdate,
        Delete: plan.ToDelete,
        Unchanged: plan.Unchanged,
        Unknown: [.. plan.Unknown.Select(u => new JsonPlanUnknown(u.ServiceName, u.Reason))],
        Skipped: [.. plan.Skipped.Select(s => new JsonSkippedResource(s.Name, s.Type, s.Reason))],
        UpToDate: plan.IsUpToDate,
        // Compose removes a project's orphaned containers on every 'up' (see StateCli.PrintLeftInPlace).
        DeletesOnApply: resolved.Config.Prune || resolved.TargetType == DeploymentTargetType.Compose,
        Blocked: blocked || strategyErrors is { Count: > 0 },
        BlockedReason: blocked
            ? resolved.ManifestAudit.BlockedSummary(strict)
            : strategyErrors is { Count: > 0 } ? string.Join(" ", strategyErrors) : null,
        ManifestFindings: [.. resolved.ManifestAudit.Diagnostics.Select(d => new JsonManifestFinding(
            d.Resource, d.Type, d.Severity switch
            {
                ManifestDiagnosticSeverity.Error => JsonSeverity.Error,
                ManifestDiagnosticSeverity.Warning => JsonSeverity.Warning,
                _ => JsonSeverity.Info
            }, d.Message))]);

    private static void PrintSection(IReadOnlyList<string> items, string heading, Color color)
    {
        if (items.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(heading);
        foreach (var item in items)
        {
            AnsiConsole.MarkupLineInterpolated($"  [bold {color}]{item}[/]");
        }

        AnsiConsole.WriteLine();
    }
}
