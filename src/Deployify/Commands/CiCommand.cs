using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Runs the full deployment pipeline in a single command, suitable for CI environments.
///
/// Steps executed in order:
///   1. generate — write artifacts from the manifest
///   2. validate — check artifacts against the target (skippable with --skip-validate)
///   3. plan — diff local artifacts against the live cluster
///   4. apply — deploy to the target (skippable with --skip-apply)
///
/// Any step failure aborts the pipeline and exits non-zero. With a health-gated rollout the
/// exit code tells what happened: 1 failed, 2 failed and rolled back, 3 rollback incomplete.
/// </summary>
public static class CiCommand
{
    public static Command Build()
    {
        var skipValidate = new Option<bool>("--skip-validate")
        {
            Description = "Skip the validate step (useful when the cluster is not reachable during CI).",
            DefaultValueFactory = _ => false
        };
        var skipApply = new Option<bool>("--skip-apply")
        {
            Description = "Stop after plan — generate and validate but do not deploy.",
            DefaultValueFactory = _ => false
        };
        var failOnChanges = new Option<bool>("--fail-on-changes")
        {
            Description = "Exit non-zero if the plan detects any create/update/delete changes (drift gating).",
            DefaultValueFactory = _ => false
        };
        var autoApprove = new Option<bool>("--auto-approve")
        {
            Description = "Deploy without asking for confirmation. Required when there is no interactive terminal.",
            DefaultValueFactory = _ => false
        };

        var cmd = new Command("ci",
            "Run the full generate → validate → plan → apply pipeline for CI/CD.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(skipValidate);
        cmd.Options.Add(skipApply);
        cmd.Options.Add(failOnChanges);
        cmd.Options.Add(autoApprove);
        cmd.Options.Add(GlobalOptions.Strict);
        RolloutCli.AddTo(cmd);
        StateCli.AddPruneOptions(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var doSkipValidate = parseResult.GetValue(skipValidate);
            var doSkipApply = parseResult.GetValue(skipApply);
            var doFailOnChanges = parseResult.GetValue(failOnChanges);
            var doAutoApprove = parseResult.GetValue(autoApprove);
            var strict = parseResult.GetValue(GlobalOptions.Strict);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    lockFor: doSkipApply ? null : "ci");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            await using var _ = resolved;
            var (_, config, envName, targetType, target, directory) = resolved;
            var prune = StateCli.PruneRequested(parseResult, config);
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLineInterpolated($"[bold]Deployify CI[/] — environment: [cyan]{envName}[/] target: [cyan]{target.DisplayName}[/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null || PlanOutput.AuditBlocks(resolved, strict))
            {
                return 1;
            }

            // ── Step 1: Generate ───────────────────────────────────────────────
            if (!await StepAsync("1/4 Generate", async stepCt =>
            {
                var paths = await target.GenerateAsync(manifest, config, outputDir, stepCt);
                AnsiConsole.MarkupLineInterpolated($"[dim] {paths.Count} artifact(s) written to {outputDir}[/]");
            }, ct))
            {
                return 1;
            }

            // ── Step 2: Validate ───────────────────────────────────────────────
            if (doSkipValidate)
            {
                AnsiConsole.MarkupLine("[dim]2/4 Validate — skipped[/]");
            }
            else if (!await StepAsync("2/4 Validate", async stepCt =>
            {
                var results = await target.ValidateAsync(manifest, config, outputDir, stepCt);
                var invalid = results.Where(r => !r.IsValid).ToList();
                if (invalid.Count > 0)
                {
                    foreach (var r in invalid)
                    {
                        foreach (var err in r.Errors)
                        {
                            AnsiConsole.MarkupLineInterpolated($"[dim] {r.ArtifactName}: {err}[/]");
                        }
                    }

                    throw new InvalidOperationException($"{invalid.Count} artifact(s) failed validation.");
                }

                AnsiConsole.MarkupLineInterpolated($"[dim] {results.Count} artifact(s) valid[/]");
            }, ct))
            {
                return 1;
            }

            // ── Step 3: Plan ───────────────────────────────────────────────────
            PlanResult? plan = null;
            if (!await StepAsync("3/4 Plan", async stepCt =>
            {
                plan = resolved.WithSkipped(await target.PlanAsync(manifest, config, outputDir, stepCt));
                AnsiConsole.MarkupLine(
                    $"[dim] +{plan.CreateCount} create / ~{plan.UpdateCount} update / -{plan.DeleteCount} delete" +
                    (plan.UnknownCount > 0 ? $" / ?{plan.UnknownCount} unchecked" : "") +
                    (plan.Skipped.Count > 0 ? $" / {plan.Skipped.Count} not deployed" : "") + "[/]");
                foreach (var u in plan.Unknown)
                {
                    AnsiConsole.MarkupLineInterpolated($"[dim]   ? {u.ServiceName}: {u.Reason}[/]");
                }

                StateCli.PrintLeftInPlace(plan, targetType, prune || doSkipApply);

                // A service that couldn't be checked may have drifted, so it counts as a change here.
                if (doFailOnChanges && !plan.IsUpToDate)
                {
                    throw new InvalidOperationException(
                        $"Plan detected changes ({plan.CreateCount} create, {plan.UpdateCount} update, " +
                        $"{plan.DeleteCount} delete, {plan.UnknownCount} could not be checked) and --fail-on-changes is set.");
                }
            }, ct))
            {
                return 1;
            }

            // ── Step 4: Apply ──────────────────────────────────────────────────
            if (doSkipApply)
            {
                AnsiConsole.MarkupLine("[dim]4/4 Apply — skipped[/]");
            }
            else if (!doAutoApprove && (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive))
            {
                AnsiConsole.MarkupLine("[red]✗[/] 4/4 Apply needs confirmation, but there is no interactive terminal. " +
                    "Pass [cyan]--auto-approve[/] to deploy from CI, or [cyan]--skip-apply[/] to stop after plan.");
                return 1;
            }
            else if (!doAutoApprove && !await ConsolePrompts.ConfirmAsync("Deploy these changes?", defaultValue: false, ct))
            {
                AnsiConsole.MarkupLine("[yellow]Apply cancelled.[/]");
                return 0;
            }
            else if (!await StateCli.RecordDeploymentAsync(resolved, manifest, ct))
            {
                return 1;
            }
            else if (await RolloutCli.ResolveAsync(parseResult, target, config, ct) is { } gated)
            {
                AnsiConsole.MarkupLine("[bold] 4/4 Apply (health-gated)[/]");
                try
                {
                    await resolved.SaveGeneratedSecretsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red] {ex.Message}[/]");
                    return 1;
                }

                var rolloutResult = await RolloutCli.RunAsync(
                    resolved, gated, manifest, outputDir, RolloutCli.ReadOptions(parseResult), ct);
                try
                {
                    await resolved.SaveSecretReferencesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Could not record secret references: {ex.Message}");
                }

                if (rolloutResult.ExitCode != 0)
                {
                    return rolloutResult.ExitCode;
                }
            }
            else if (!await StepAsync("4/4 Apply", async stepCt =>
            {
                await resolved.SaveGeneratedSecretsAsync(stepCt);
                var results = await target.ApplyAsync(manifest, config, outputDir, stepCt);
                await resolved.SaveSecretReferencesAsync(stepCt);
                var failed = results.Where(r => !r.Success).ToList();
                foreach (var r in results)
                {
                    if (r.Success)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[dim] ✓ {r.ServiceName}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated($"[dim] ✗ {r.ServiceName}: {r.Error ?? ""}[/]");
                    }
                }

                if (failed.Count > 0)
                {
                    throw new InvalidOperationException($"{failed.Count} service(s) failed to deploy.");
                }
            }, ct))
            {
                return 1;
            }

            if (prune && !doSkipApply)
            {
                var pruned = await StateCli.PruneAsync(resolved, manifest, plan, parseResult.GetValue(StateCli.ForcePrune), ct);
                if (pruned != 0)
                {
                    return pruned;
                }
            }

            AnsiConsole.MarkupLine("\n[green]✓ CI pipeline complete.[/]");

            return 0;
        });

        return cmd;
    }

    private static async Task<bool> StepAsync(string label, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        AnsiConsole.MarkupInterpolated($"[bold] {label}[/]... ");
        try
        {
            await step(ct);
            AnsiConsole.MarkupLine("[green]✓[/]");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]✗[/]");
            AnsiConsole.MarkupLineInterpolated($"[red] {ex.Message}[/]");
            return false;
        }
    }
}
