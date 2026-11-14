using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

public static class ApplyCommand
{
    public static Command Build()
    {
        var autoApprove = new Option<bool>("--auto-approve")
        {
            Description = "Skip confirmation prompt.",
            DefaultValueFactory = _ => false
        };

        var imageTagOpt = new Option<string?>("--image-tag", "-i")
        {
            Description = "Deploy this image tag instead of the environment's configured tag, for example one promoted from another environment."
        };

        var cmd = new Command("apply", "Deploy to the target.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(GlobalOptions.Strict);
        cmd.Options.Add(autoApprove);
        cmd.Options.Add(imageTagOpt);
        cmd.Options.Add(GlobalOptions.RequireDigest);
        RolloutCli.AddTo(cmd);
        StateCli.AddPruneOptions(cmd);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var exitCode = 0;
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var envOverride = parseResult.GetValue(GlobalOptions.Environment);
            var targetOpt = parseResult.GetValue(GlobalOptions.Target);
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            var strict = parseResult.GetValue(GlobalOptions.Strict);
            var approve = parseResult.GetValue(autoApprove);
            var tagOverride = parseResult.GetValue(imageTagOpt)?.Trim();
            var requireDigest = parseResult.GetValue(GlobalOptions.RequireDigest);

            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, envOverride, targetOpt, ct,
                    lockFor: dryRun ? null : "apply");
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
            if (!string.IsNullOrEmpty(tagOverride))
            {
                config.ImageTag = tagOverride;
            }

            AnsiConsole.MarkupLineInterpolated($"[dim]Environment: [cyan]{envName}[/] Target: [cyan]{target.DisplayName}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null || PlanOutput.UnpinnedBlocks(manifest, config, requireDigest))
            {
                return 1;
            }

            // PlanAsync writes artifacts to disk AND diffs against the live cluster.
            // If the cluster is unreachable during dry-run we still want to exit 0
            // because the artifact generation phase completed successfully.
            PlanResult? plan = null;
            try
            {
                plan = resolved.WithSkipped(await target.PlanAsync(manifest, config, outputDir, ct));
                PlanOutput.PrintUnknown(plan);
                AnsiConsole.MarkupLine(PlanOutput.Summary(plan, "[bold]Plan:[/]"));
                StateCli.PrintLeftInPlace(plan, targetType, prune);
            }
            catch (Exception ex) when (dryRun)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Could not reach cluster for diff: {ex.Message}");
                AnsiConsole.MarkupLine("[yellow]Dry-run: artifacts written, no changes applied.[/]");
                return 0;
            }

            if (PlanOutput.AuditBlocks(resolved, strict))
            {
                return 1;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run: no changes applied.[/]");
                return 0;
            }

            if (!approve && !await ConsolePrompts.ConfirmAsync(
                    $"Apply to [bold cyan]{Markup.Escape(envName)}[/] ({Markup.Escape(target.DisplayName)})?", ct))
            {
                AnsiConsole.MarkupLine("Aborted.");
                return 0;
            }

            if (!await StateCli.RecordDeploymentAsync(resolved, manifest, ct))
            {
                return 1;
            }

            try
            {
                await resolved.SaveGeneratedSecretsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            if (await RolloutCli.ResolveAsync(parseResult, target, config, ct) is { } rollout)
            {
                var rolloutResult = await RolloutCli.RunAsync(
                    resolved, rollout, manifest, outputDir, RolloutCli.ReadOptions(parseResult), ct);

                // Not cancellable: a cancelled rollout has still changed (and maybe restored) secrets.
                try
                {
                    await resolved.SaveSecretReferencesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Could not record secret references: {ex.Message}");
                }

                if (rolloutResult.ExitCode != 0 || !prune)
                {
                    return rolloutResult.ExitCode;
                }

                return await StateCli.PruneAsync(resolved, manifest, plan, parseResult.GetValue(StateCli.ForcePrune), ct);
            }

            var results = await target.ApplyAsync(manifest, config, outputDir, ct);

            try
            {
                await resolved.SaveSecretReferencesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]![/] Could not record secret references: {ex.Message}");
            }

            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);

            foreach (var r in results)
            {
                if (r.Success)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(r.ServiceName)}" +
                        (r.DeploymentId is not null ? $" [dim]({Markup.Escape(r.DeploymentId)})[/]" : ""));
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"  [red]✗[/] {r.ServiceName}: {r.Error ?? ""}");
                }
            }

            if (failed == 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"\n[green]Apply complete.[/] {succeeded} service(s) deployed to [cyan]{envName}[/].");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"\n[yellow]Apply finished with errors.[/] {succeeded} succeeded, {failed} failed.");
            }

            if (failed > 0)
            {
                exitCode = 1;
            }
            else if (prune)
            {
                exitCode = await StateCli.PruneAsync(resolved, manifest, plan, parseResult.GetValue(StateCli.ForcePrune), ct);
            }

            return exitCode;
        });

        return cmd;
    }
}
