using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Runs the full deployment pipeline in a single command, suitable for CI environments.
///
/// Steps executed in order:
///   1. generate — write artefacts from the manifest
///   2. validate — check artefacts against the target (skippable with --skip-validate)
///   3. plan — diff local artefacts against the live cluster
///   4. apply — deploy to the target (skippable with --skip-apply)
///
/// Any step failure aborts the pipeline and exits non-zero.
/// </summary>
public static class CiCommand
{
    public static Command Build()
    {
        var skipValidate = new Option<bool>("--skip-validate",
            () => false,
            "Skip the validate step (useful when the cluster is not reachable during CI).");
        var skipApply = new Option<bool>("--skip-apply",
            () => false,
            "Stop after plan — generate and validate but do not deploy.");
        var failOnChanges = new Option<bool>("--fail-on-changes",
            () => false,
            "Exit non-zero if the plan detects any create/update/delete changes (drift gating).");

        var cmd = new Command("ci",
            "Run the full generate → validate → plan → apply pipeline for CI/CD.");
        cmd.AddOption(GlobalOptions.ProjectPath);
        cmd.AddOption(GlobalOptions.Environment);
        cmd.AddOption(GlobalOptions.Target);
        cmd.AddOption(skipValidate);
        cmd.AddOption(skipApply);
        cmd.AddOption(failOnChanges);

        cmd.SetHandler(async ctx =>
        {
            var projectPath = ctx.ParseResult.GetValueForOption(GlobalOptions.ProjectPath)!;
            var envOverride = ctx.ParseResult.GetValueForOption(GlobalOptions.Environment);
            var targetOpt = ctx.ParseResult.GetValueForOption(GlobalOptions.Target);
            var doSkipValidate = ctx.ParseResult.GetValueForOption(skipValidate);
            var doSkipApply = ctx.ParseResult.GetValueForOption(skipApply);
            var doFailOnChanges = ctx.ParseResult.GetValueForOption(failOnChanges);

            ResolvedContext resolved;
            try { resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {ex.Message}"); ctx.ExitCode = 1; return; }

            var (_, config, envName, _, target, directory) = resolved;
            var outputDir = Path.Combine(directory, config.OutputPath);

            AnsiConsole.MarkupLine($"[bold]Deployify CI[/] — environment: [cyan]{envName}[/] target: [cyan]{target.DisplayName}[/]\n");

            var manifest = await resolved.LoadManifestAsync();
            if (manifest is null) { ctx.ExitCode = 1; return; }

            // ── Step 1: Generate ───────────────────────────────────────────────
            if (!await StepAsync("1/4 Generate", async () =>
            {
                var paths = await target.GenerateAsync(manifest, config, outputDir);
                AnsiConsole.MarkupLine($"[dim] {paths.Count} artefact(s) written to {outputDir}[/]");
            })) { ctx.ExitCode = 1; return; }

            // ── Step 2: Validate ───────────────────────────────────────────────
            if (doSkipValidate)
            {
                AnsiConsole.MarkupLine("[dim]2/4 Validate — skipped[/]");
            }
            else if (!await StepAsync("2/4 Validate", async () =>
            {
                var results = await target.ValidateAsync(manifest, config, outputDir);
                var invalid = results.Where(r => !r.IsValid).ToList();
                if (invalid.Count > 0)
                {
                    foreach (var r in invalid)
                    {
	                    foreach (var err in r.Errors)
	                    {
		                    AnsiConsole.MarkupLine($"[dim] {r.ArtefactName}: {err}[/]");
	                    }
                    }

                    throw new InvalidOperationException($"{invalid.Count} artefact(s) failed validation.");
                }

                AnsiConsole.MarkupLine($"[dim] {results.Count} artefact(s) valid[/]");
            })) { ctx.ExitCode = 1; return; }

            // ── Step 3: Plan ───────────────────────────────────────────────────
            if (!await StepAsync("3/4 Plan", async () =>
                {
	                var plan = await target.PlanAsync(manifest, config, outputDir);
	                AnsiConsole.MarkupLine(
		                $"[dim] +{plan.CreateCount} create / ~{plan.UpdateCount} update / -{plan.DeleteCount} delete[/]");

	                if (doFailOnChanges && (plan.CreateCount + plan.UpdateCount + plan.DeleteCount) > 0)
	                {
		                throw new InvalidOperationException(
			                $"Plan detected changes ({plan.CreateCount} create, {plan.UpdateCount} update, " +
			                $"{plan.DeleteCount} delete) and --fail-on-changes is set.");
	                }
                })) { ctx.ExitCode = 1; return; }

            // ── Step 4: Apply ──────────────────────────────────────────────────
            if (doSkipApply)
            {
                AnsiConsole.MarkupLine("[dim]4/4 Apply — skipped[/]");
            }
            else if (!await StepAsync("4/4 Apply", async () =>
            {
                var results = await target.ApplyAsync(manifest, config, outputDir);
                var failed = results.Where(r => !r.Success).ToList();
                foreach (var r in results)
                {
	                AnsiConsole.MarkupLine(r.Success
		                ? $"[dim] ✓ {r.ServiceName}[/]"
		                : $"[dim] ✗ {r.ServiceName}: {r.Error}[/]");
                }

                if (failed.Count > 0)
                {
	                throw new InvalidOperationException($"{failed.Count} service(s) failed to deploy.");
                }
            })) { ctx.ExitCode = 1; return; }

            AnsiConsole.MarkupLine("\n[green]✓ CI pipeline complete.[/]");
        });

        return cmd;
    }

    private static async Task<bool> StepAsync(string label, Func<Task> step)
    {
        AnsiConsole.Markup($"[bold] {label}[/]... ");
        try
        {
            await step();
            AnsiConsole.MarkupLine("[green]✓[/]");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]✗[/]");
            AnsiConsole.MarkupLine($"[red] {ex.Message}[/]");
            return false;
        }
    }
}
