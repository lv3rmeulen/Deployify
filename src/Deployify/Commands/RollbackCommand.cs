using System;
using System.CommandLine;
using System.Linq;
using System.Threading;
using Deployify.Core.Interfaces;
using Deployify.Core.Rollouts;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Puts every service an earlier rollout touched back on the revision it ran before that
/// rollout, using the snapshots in its journal. Rolls back the latest rollout unless
/// <c>--rollout</c> names another one.
/// </summary>
public static class RollbackCommand
{
    public static Command Build()
    {
        var rolloutId = new Option<string?>("--rollout")
        {
            Description = "Id of the rollout to undo (see 'deployify status'). Defaults to the latest one."
        };
        var autoApprove = new Option<bool>("--auto-approve")
        {
            Description = "Roll back without asking for confirmation. Required when there is no interactive terminal.",
            DefaultValueFactory = _ => false
        };

        var cmd = new Command("rollback", "Undo a rollout: restore the revisions that ran before it.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(rolloutId);
        cmd.Options.Add(autoApprove);
        cmd.Options.Add(RolloutCli.HealthTimeout);
        cmd.Options.Add(RolloutCli.RollbackTimeout);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(
                    parseResult.GetValue(GlobalOptions.ProjectPath)!,
                    parseResult.GetValue(GlobalOptions.Environment),
                    parseResult.GetValue(GlobalOptions.Target), ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            if (resolved.Target is not IRolloutTarget rollout)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {resolved.Target.DisplayName} has no health-gated rollouts, so there is nothing to roll back.");
                return 1;
            }

            var rollouts = await RolloutJournal.ListAsync(resolved.Directory, resolved.EnvName, ct);
            var wanted = parseResult.GetValue(rolloutId);
            var chosen = wanted is null
                ? rollouts.FirstOrDefault()
                : rollouts.FirstOrDefault(r => r.Record.Id == wanted);
            if (chosen.Record is null)
            {
                AnsiConsole.MarkupLineInterpolated(wanted is null
                    ? (FormattableString)$"[red]✗[/] No rollouts recorded for [cyan]{resolved.EnvName}[/]."
                    : (FormattableString)$"[red]✗[/] Rollout '{wanted}' not found for [cyan]{resolved.EnvName}[/]. See 'deployify status'.");

                return 1;
            }

            var (path, record) = chosen;
            if (record.Target != resolved.Target.DisplayName)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]✗[/] Rollout {record.Id} went to {record.Target}, but this environment now deploys to {resolved.Target.DisplayName}.");
                return 1;
            }

            var toRestore = record.Services
                .Where(s => s.Snapshot is not null &&
                            s.State is not (ServiceRolloutState.RolledBack or ServiceRolloutState.RevertedByPlatform))
                .ToList();
            if (toRestore.Count == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"Rollout {record.Id} has nothing left to roll back ({record.State}).");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated(
                $"Rollout [cyan]{record.Id}[/] ({record.State}, started {record.StartedAt.ToLocalTime():g}) will be undone:");
            foreach (var s in toRestore)
            {
                var to = s.Snapshot!.Existed ? $"back to {s.Snapshot.Revision}" : "taken out of service (new in this rollout)";
                AnsiConsole.MarkupLineInterpolated($"  {s.Name}: {to}");
            }

            if (rollouts.Count > 0 && rollouts[0].Record.Id != record.Id)
            {
                AnsiConsole.MarkupLine("[yellow]![/] Newer rollouts exist; their changes to these services will be undone too.");
            }

            if (!parseResult.GetValue(autoApprove))
            {
                if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
                {
                    AnsiConsole.MarkupLine("[red]✗[/] Rollback needs confirmation, but there is no interactive terminal. Pass [cyan]--auto-approve[/].");
                    return 1;
                }

                if (!await ConsolePrompts.ConfirmAsync("Roll back?", defaultValue: false, ct))
                {
                    AnsiConsole.MarkupLine("Aborted.");
                    return 0;
                }
            }

            RolloutJournal journal;
            try
            {
                journal = await RolloutJournal.OpenAsync(resolved.Directory, path, ct);
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            await using (journal)
            {
                // The journal on disk is the one to update; match its service records by name.
                var services = toRestore.Select(s => journal.Record.Service(s.Name)).ToList();
                await journal.LogAsync(null, "manual rollback requested");

                var options = new RolloutOptions
                {
                    HealthTimeout = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(RolloutCli.HealthTimeout))),
                    RollbackTimeout = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(RolloutCli.RollbackTimeout)))
                };
                var ok = await RolloutEngine.RollBackAsync(rollout, resolved.Config, options, journal, services,
                    (kind, service, message) => RolloutCli.Print(new RolloutProgress(kind, service, message)));

                journal.Record.State = ok ? RolloutState.RolledBack : RolloutState.RollbackFailed;
                journal.Record.FinishedAt = DateTimeOffset.UtcNow;
                await journal.SaveAsync(CancellationToken.None);

                AnsiConsole.WriteLine();
                if (ok)
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]Rollback complete.[/] {services.Count} service(s) restored in [cyan]{resolved.EnvName}[/].");
                    return 0;
                }

                var stuck = string.Join(", ", services.Where(s => s.State == ServiceRolloutState.RollbackFailed).Select(s => s.Name));
                AnsiConsole.MarkupLineInterpolated($"[red]Rollback did not complete.[/] Check these services by hand: {stuck}");
                return 3;
            }
        });

        return cmd;
    }
}
