using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Lifecycle;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.Services;
using Deployify.Core.State;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// <c>deployify state</c>: look at and repair an environment's deployment state.
/// <list type="bullet">
/// <item><c>show</c>: where state lives, who holds the lock, what is recorded as deployed.</item>
/// <item><c>unlock &lt;id&gt;</c>: free a lock left by a run that died.</item>
/// <item><c>adopt</c>: record services Deployify deployed before it kept an inventory.</item>
/// <item><c>push</c>: copy local state into a newly configured remote backend.</item>
/// <item><c>migrate-secrets</c>: replace plain-text secrets in deployify-state.json with env: references.</item>
/// </list>
/// </summary>
public static class StateCommand
{
    public static Command Build()
    {
        var cmd = new Command("state", "Inspect and manage deployment state (inventory, lock, remote backend).");
        cmd.Subcommands.Add(BuildShow());
        cmd.Subcommands.Add(BuildUnlock());
        cmd.Subcommands.Add(BuildAdopt());
        cmd.Subcommands.Add(BuildPush());
        cmd.Subcommands.Add(BuildMigrateSecrets());
        return cmd;
    }

    private static Command WithCommon(Command cmd)
    {
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        return cmd;
    }

    private static async Task<ResolvedContext?> ResolveAsync(ParseResult parseResult, CancellationToken ct, string? lockFor = null)
    {
        try
        {
            return await DependencyInjection.ResolveAsync(
                parseResult.GetValue(GlobalOptions.ProjectPath)!,
                parseResult.GetValue(GlobalOptions.Environment),
                parseResult.GetValue(GlobalOptions.Target), ct, lockFor);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
            return null;
        }
    }

    // ── show ──────────────────────────────────────────────────────────────────

    private static Command BuildShow()
    {
        var cmd = WithCommon(new Command("show", "Show where state is kept, the lock, and what is recorded as deployed."));
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (await ResolveAsync(parseResult, ct) is not { } resolved)
            {
                return 1;
            }

            await using var _ = resolved;
            var state = resolved.State;
            AnsiConsole.MarkupLineInterpolated($"Environment: [cyan]{resolved.EnvName}[/]");
            AnsiConsole.MarkupLineInterpolated($"Backend:     {state.Config.Backend} — {state.Backend.Description}");
            AnsiConsole.MarkupLineInterpolated($"Keys:        {state.Root}");

            try
            {
                StateCli.PrintLock(await StateLease.PeekAsync(state.Backend, state.LockKey, ct: ct));
                var (inventory, etag) = await state.Inventory.ReadAsync(ct);
                if (etag is null)
                {
                    AnsiConsole.MarkupLine("Inventory:   [dim]none yet (written by the first apply)[/]");
                    return 0;
                }

                AnsiConsole.MarkupLineInterpolated(
                    $"Inventory:   serial {inventory.Serial}, lineage {inventory.Lineage[..8]}, updated {inventory.UpdatedAt.ToLocalTime():g}");
                var table = new Table().AddColumns("Service", "Depends on", "First deployed", "Last deployed", "Target id");
                foreach (var r in inventory.Services.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    table.AddRow(
                        Markup.Escape(r.Name),
                        Markup.Escape(string.Join(", ", r.DependsOn)),
                        Markup.Escape(r.CreatedAt.ToLocalTime().ToString("g")),
                        Markup.Escape(r.UpdatedAt.ToLocalTime().ToString("g")),
                        Markup.Escape(r.TargetId ?? ""));
                }

                AnsiConsole.Write(table);
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not read state: {ex.Message}");
                return 1;
            }
        });
        return cmd;
    }

    // ── unlock ────────────────────────────────────────────────────────────────

    private static Command BuildUnlock()
    {
        var lockId = new Argument<string>("lock-id") { Description = "The id of the lock to remove, as 'state show' or the lock error prints it." };
        var cmd = WithCommon(new Command("unlock", "Remove a lock left behind by a run that is no longer going."));
        cmd.Arguments.Add(lockId);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (await ResolveAsync(parseResult, ct) is not { } resolved)
            {
                return 1;
            }

            await using var _ = resolved;
            var id = parseResult.GetValue(lockId)!;
            try
            {
                if (await StateLease.ForceUnlockAsync(resolved.State.Backend, resolved.State.LockKey, id, ct))
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Lock {id} on [cyan]{resolved.EnvName}[/] removed.");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"[cyan]{resolved.EnvName}[/] is not locked.");
                }

                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }
        });
        return cmd;
    }

    // ── adopt ─────────────────────────────────────────────────────────────────

    private static Command BuildAdopt()
    {
        var services = new Option<string[]>("--service", "-s")
        {
            Description = "Only adopt these services (comma-separated or repeated). Default: all of them.",
            AllowMultipleArgumentsPerToken = true
        };
        var cmd = WithCommon(new Command("adopt",
            "Record services the target marks as Deployify's but state doesn't list yet (deployed before Deployify kept an inventory), so destroy and prune can remove them."));
        cmd.Options.Add(services);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (await ResolveAsync(parseResult, ct, lockFor: "adopt") is not { } resolved)
            {
                return 1;
            }

            await using var _ = resolved;
            if (resolved.Target.GetCapability<IResourceLifecycle>() is not { } lifecycle)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {resolved.Target.DisplayName} doesn't support this yet.");
                return 1;
            }

            try
            {
                var managed = await lifecycle.ListManagedAsync(resolved.Config, ct);
                var only = StateCli.SplitList(parseResult.GetValue(services));
                var added = await InventoryRecorder.AdoptAsync(resolved.State, resolved.Target, managed, resolved.EnvName,
                    only.Count > 0 ? only.ToHashSet(StringComparer.Ordinal) : null, ct: ct);
                if (added.Count == 0)
                {
                    AnsiConsole.MarkupLine("Nothing to adopt: state already lists every service marked as Deployify's.");
                    return 0;
                }

                foreach (var a in added)
                {
                    AnsiConsole.MarkupLineInterpolated($"  [green]+[/] {a}");
                }

                AnsiConsole.MarkupLineInterpolated($"[green]✓[/] Adopted {added.Count} service(s) into [cyan]{resolved.EnvName}[/]'s state.");
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }
        });
        return cmd;
    }

    // ── push ──────────────────────────────────────────────────────────────────

    private static Command BuildPush()
    {
        var force = new Option<bool>("--force")
        {
            Description = "Replace remote state even when it belongs to another lineage or is newer.",
            DefaultValueFactory = _ => false
        };
        var cmd = WithCommon(new Command("push",
            "Copy local state (inventory, generated parameters, rollout journals) into the remote backend configured for the environment. Use it once, after switching an existing environment to remote state."));
        cmd.Options.Add(force);
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (await ResolveAsync(parseResult, ct, lockFor: "push") is not { } resolved)
            {
                return 1;
            }

            await using var _ = resolved;
            var remote = resolved.State;
            if (!remote.IsRemote)
            {
                AnsiConsole.MarkupLine("[red]✗[/] This environment keeps its state locally; configure a remote backend under \"State\" first.");
                return 1;
            }

            try
            {
                var local = new StateSession(resolved.Directory, resolved.EnvName, new StateBackendConfig { Prefix = remote.Config.Prefix });
                var (localInventory, localTag) = await local.Inventory.ReadAsync(ct);
                var (remoteInventory, remoteTag) = await remote.Inventory.ReadAsync(ct);
                var forced = parseResult.GetValue(force);

                if (localTag is not null)
                {
                    if (remoteTag is not null && !forced && remoteInventory.Lineage != localInventory.Lineage)
                    {
                        AnsiConsole.MarkupLine("[red]✗[/] Remote state already has an inventory from another lineage. Pass [cyan]--force[/] to replace it.");
                        return 1;
                    }

                    if (remoteTag is not null && !forced && remoteInventory.Serial > localInventory.Serial)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"[red]✗[/] Remote state is newer (serial {remoteInventory.Serial} > {localInventory.Serial}). Pass [cyan]--force[/] to replace it.");
                        return 1;
                    }

                    await remote.Inventory.ReplaceAsync(localInventory, remoteTag, ct);
                    AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] inventory ({localInventory.Services.Count()} service(s))");
                }

                await remote.PushParametersAsync(ct);
                AnsiConsole.MarkupLine("  [green]✓[/] generated parameters");

                var journals = await RolloutJournal.ListAsync(resolved.Directory, resolved.EnvName, ct);
                foreach (var (_, record) in journals)
                {
                    await remote.PushJournalAsync(record, ct);
                }

                AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] {journals.Count} journal(s)");
                AnsiConsole.MarkupLineInterpolated($"[green]✓[/] State of [cyan]{resolved.EnvName}[/] pushed to {remote.Backend.Description}.");
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]The local copies stay in {Path.Combine(resolved.Directory, ".deployify")}; deployify-parameters.json holds secrets, keep it out of git.[/]");
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }
        });
        return cmd;
    }

    // ── migrate-secrets ───────────────────────────────────────────────────────

    private static Command BuildMigrateSecrets()
    {
        var dryRunOpt = new Option<bool>("--dry-run")
        {
            Description = "Show what would change without writing the file or printing secret values."
        };
        var cmd = new Command("migrate-secrets",
            "Replace tokens and passwords stored as plain text in deployify-state.json with env:NAME references.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(dryRunOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var directory = InitCommand.ResolveDirectory(parseResult.GetValue(GlobalOptions.ProjectPath)!);
            var dryRun = parseResult.GetValue(dryRunOpt);
            var stateManager = new StateManager();
            if (!stateManager.Exists(directory))
            {
                AnsiConsole.MarkupLine("[red]✗[/] deployify-state.json not found. Run 'deployify init' first.");
                return 1;
            }

            var stateFile = await stateManager.LoadFileAsync(directory, ct);
            var changes = StateFileSecrets.MigrateToReferences(stateFile);
            if (changes.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]✓[/] deployify-state.json has no plain-text secrets.");
                return 0;
            }

            var table = new Table().AddColumns("Setting", "Now reads");
            foreach (var c in changes)
            {
                table.AddRow(Markup.Escape(c.Path), Markup.Escape(Secrets.EnvPrefix + c.Variable));
            }

            AnsiConsole.Write(table);
            if (dryRun)
            {
                AnsiConsole.MarkupLine("[dim]Dry run: nothing was changed.[/]");
                return 0;
            }

            await stateManager.SaveFileAsync(stateFile, directory, ct);
            AnsiConsole.MarkupLineInterpolated($"\n[green]✓[/] Moved {changes.Count} secret(s) out of deployify-state.json.");
            AnsiConsole.MarkupLine("Set these variables wherever you run Deployify (your user environment, CI secrets):\n");

            // Plain Console, not markup: the values may contain [ ] and must print exactly.
            foreach (var (variable, value) in changes
                         .GroupBy(c => c.Variable, StringComparer.Ordinal)
                         .Select(g => (g.Key, g.First().Value)))
            {
                Console.WriteLine("  " + ShellSnippets.SetVariable(variable, value));
            }

            AnsiConsole.MarkupLine("\n[yellow]![/] The old values are still in your git history if deployify-state.json " +
                                   "was ever committed. Rotate those tokens and passwords.");
            return 0;
        });
        return cmd;
    }
}
