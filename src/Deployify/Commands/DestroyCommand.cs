using System;
using System.CommandLine;
using System.Linq;
using Deployify.Core.Interfaces;
using Deployify.Core.Lifecycle;
using Deployify.Core.Rollouts;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Removes what Deployify deployed in an environment: every service that is both in the
/// environment's state and marked as Deployify's on the target, dependents first. Data is kept
/// by default (volumes, registry images, external secret stores). Running it again after a
/// failure continues where it stopped.
/// </summary>
public static class DestroyCommand
{
    public static Command Build()
    {
        var services = new Option<string[]>("--service", "-s")
        {
            Description = "Only destroy these services (comma-separated or repeated). Default: all of them.",
            AllowMultipleArgumentsPerToken = true
        };
        var keepSecrets = new Option<bool>("--keep-secrets")
        {
            Description = "Keep the services' platform-side secrets (Nomad variables, SSM parameters).",
            DefaultValueFactory = _ => false
        };
        var volumes = new Option<bool>("--volumes")
        {
            Description = "Also remove data volumes (Compose). They are kept by default.",
            DefaultValueFactory = _ => false
        };
        var autoApprove = new Option<bool>("--auto-approve")
        {
            Description = "Destroy without typing the environment name to confirm. Required when there is no interactive terminal.",
            DefaultValueFactory = _ => false
        };

        var cmd = new Command("destroy", "Remove everything Deployify deployed in an environment.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(services);
        cmd.Options.Add(keepSecrets);
        cmd.Options.Add(volumes);
        cmd.Options.Add(autoApprove);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(
                    parseResult.GetValue(GlobalOptions.ProjectPath)!,
                    parseResult.GetValue(GlobalOptions.Environment),
                    parseResult.GetValue(GlobalOptions.Target), ct,
                    lockFor: dryRun ? null : "destroy");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            await using var _ = resolved;
            var (_, config, envName, _, target, _) = resolved;

            if (config.Protected)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] Environment [cyan]{envName}[/] is protected. Set \"Protected\": false in deployify-state.json first if you really mean to destroy it.");
                return 1;
            }

            if (target is not IResourceLifecycle lifecycle)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {target.DisplayName} doesn't support destroy yet.");
                return 1;
            }

            var only = StateCli.SplitList(parseResult.GetValue(services));
            var options = new DeleteOptions
            {
                KeepSecrets = parseResult.GetValue(keepSecrets),
                RemoveVolumes = parseResult.GetValue(volumes)
            };

            RemovalPlan plan;
            try
            {
                var inventory = await resolved.State.Inventory.LoadAsync(ct);
                var managed = await lifecycle.ListManagedAsync(config, ct);
                var known = inventory.Services.Select(s => s.Name).Concat(managed.Select(m => m.Name)).ToHashSet(StringComparer.Ordinal);
                if (only.FirstOrDefault(s => !known.Contains(s)) is { } unknown)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[red]✗[/] '{unknown}' is neither in {envName}'s state nor marked as Deployify's on {target.DisplayName}.");
                    return 1;
                }

                plan = RemovalPlanner.Build(only.Count > 0 ? only : known, inventory, managed, envName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not find out what Deployify deployed: {ex.Message}");
                return 1;
            }

            // Environment-wide leftovers only go when the whole environment does and nothing of ours is left behind.
            var wholeEnvironment = only.Count == 0 && plan.NotInState.Count == 0 && plan.OtherEnvironment.Count == 0;

            AnsiConsole.MarkupLineInterpolated($"[bold]Destroy plan[/] for [cyan]{envName}[/] on {target.DisplayName}:");
            StateCli.PrintRemovalPlan(plan, "destroy");
            var kept = lifecycle.DescribeKept(config, options);
            if (kept.Count > 0)
            {
                AnsiConsole.MarkupLine("[dim]Kept:[/]");
                foreach (var k in kept)
                {
                    AnsiConsole.MarkupLineInterpolated($"[dim]  · {k}[/]");
                }
            }

            if (plan.IsEmpty)
            {
                AnsiConsole.MarkupLine("Nothing to destroy.");
                return 0;
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run: nothing was removed.[/]");
                return 0;
            }

            if (!parseResult.GetValue(autoApprove))
            {
                if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
                {
                    AnsiConsole.MarkupLine("[red]✗[/] Destroy needs confirmation, but there is no interactive terminal. Pass [cyan]--auto-approve[/].");
                    return 1;
                }

                var typed = await ConsolePrompts.AskAsync<string>(
                    $"Type the environment name ([cyan]{Markup.Escape(envName)}[/]) to destroy it:", ct);
                if (typed.Trim() != envName)
                {
                    AnsiConsole.MarkupLine("Aborted.");
                    return 0;
                }
            }

            var result = await ServiceRemover.RunAsync(new RemovalRequest
            {
                Target = target,
                Lifecycle = lifecycle,
                Config = config,
                EnvironmentName = envName,
                State = resolved.State,
                Plan = plan,
                Operation = RolloutOperation.Destroy,
                Options = options,
                WholeEnvironment = wholeEnvironment,
                OnProgress = RolloutCli.Print
            }, ct);

            AnsiConsole.WriteLine();
            if (result.Succeeded)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Destroy complete.[/] {result.Deleted.Count} service(s) removed from [cyan]{envName}[/].");
                AnsiConsole.MarkupLineInterpolated($"[dim]Journal: {result.JournalPath ?? ""}[/]");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated(
                $"[red]Destroy stopped at {result.FailedService ?? "?"}:[/] {result.Error ?? ""}");
            AnsiConsole.MarkupLine("[dim]Fix the cause and run destroy again; it continues with what is left.[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]Journal: {result.JournalPath ?? ""}[/]");
            return 1;
        });

        return cmd;
    }
}
