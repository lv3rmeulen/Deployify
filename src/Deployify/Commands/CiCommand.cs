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
/// Any step failure aborts the pipeline and exits non-zero.
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

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var doSkipValidate = parseResult.GetValue(skipValidate);
            var doSkipApply = parseResult.GetValue(skipApply);
            var doFailOnChanges = parseResult.GetValue(failOnChanges);
            var doAutoApprove = parseResult.GetValue(autoApprove);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[bold]Deployify CI[/] — environment: [cyan]{envName}[/] target: [cyan]{target.DisplayName}[/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            // ── Step 1: Generate ───────────────────────────────────────────────
            if (!await StepAsync("1/4 Generate", async stepCt =>
            {
                var paths = await target.GenerateAsync(manifest, config, outputDir, stepCt);
                AnsiConsole.MarkupLine($"[dim] {paths.Count} artifact(s) written to {Markup.Escape(outputDir)}[/]");
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
                            AnsiConsole.MarkupLine($"[dim] {Markup.Escape(r.ArtifactName)}: {Markup.Escape(err)}[/]");
                        }
                    }

                    throw new InvalidOperationException($"{invalid.Count} artifact(s) failed validation.");
                }

                AnsiConsole.MarkupLine($"[dim] {results.Count} artifact(s) valid[/]");
            }, ct))
            {
                return 1;
            }

            // ── Step 3: Plan ───────────────────────────────────────────────────
            PlanResult? plan;
            if (!await StepAsync("3/4 Plan", async stepCt =>
            {
                plan = await target.PlanAsync(manifest, config, outputDir, stepCt);
                AnsiConsole.MarkupLine(
                    $"[dim] +{plan.CreateCount} create / ~{plan.UpdateCount} update / -{plan.DeleteCount} delete[/]");

                if (doFailOnChanges && (plan.CreateCount + plan.UpdateCount + plan.DeleteCount) > 0)
                {
                    throw new InvalidOperationException(
                        $"Plan detected changes ({plan.CreateCount} create, {plan.UpdateCount} update, " +
                        $"{plan.DeleteCount} delete) and --fail-on-changes is set.");
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
            else if (!await StepAsync("4/4 Apply", async stepCt =>
            {
                var results = await target.ApplyAsync(manifest, config, outputDir, stepCt);
                var failed = results.Where(r => !r.Success).ToList();
                foreach (var r in results)
                {
                    AnsiConsole.MarkupLine(r.Success
                        ? $"[dim] ✓ {r.ServiceName}[/]"
                        : $"[dim] ✗ {Markup.Escape(r.ServiceName)}: {Markup.Escape(r.Error ?? "")}[/]");
                }

                if (failed.Count > 0)
                {
                    throw new InvalidOperationException($"{failed.Count} service(s) failed to deploy.");
                }
            }, ct))
            {
                return 1;
            }

            AnsiConsole.MarkupLine("\n[green]✓ CI pipeline complete.[/]");

            return 0;
        });

        return cmd;
    }

    private static async Task<bool> StepAsync(string label, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        AnsiConsole.Markup($"[bold] {label}[/]... ");
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
            AnsiConsole.MarkupLine($"[red] {Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }
}
