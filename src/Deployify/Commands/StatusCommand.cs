using System;
using System.CommandLine;
using System.Linq;
using Deployify.Core.Rollouts;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>Shows recorded rollouts for the environment, or the details of one.</summary>
public static class StatusCommand
{
    public static Command Build()
    {
        var rolloutId = new Option<string?>("--rollout")
        {
            Description = "Show the services and events of this rollout instead of the list."
        };
        var limit = new Option<int>("--limit")
        {
            Description = "How many rollouts to list (default: 10).",
            DefaultValueFactory = _ => 10
        };

        var cmd = new Command("status", "Show recent rollouts and how they ended.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.Environment);
        cmd.Options.Add(GlobalOptions.Target);
        cmd.Options.Add(rolloutId);
        cmd.Options.Add(limit);

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

            var rollouts = await RolloutJournal.ListAsync(resolved.Directory, resolved.EnvName, ct);
            var wanted = parseResult.GetValue(rolloutId);

            if (wanted is null)
            {
                if (rollouts.Count == 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"No rollouts recorded for [cyan]{resolved.EnvName}[/].");
                    return 0;
                }

                var list = new Table().AddColumns("Rollout", "Operation", "Started", "State", "Reason");
                foreach (var (_, r) in rollouts.Take(Math.Max(1, parseResult.GetValue(limit))))
                {
                    list.AddRow(
                        Markup.Escape(r.Id),
                        Markup.Escape(r.Operation.ToString().ToLowerInvariant()),
                        Markup.Escape(r.StartedAt.ToLocalTime().ToString("g")),
                        StateMarkup(r.State),
                        Markup.Escape(r.Reason ?? ""));
                }

                AnsiConsole.Write(list);
                return 0;
            }

            var match = rollouts.FirstOrDefault(r => r.Record.Id == wanted);
            if (match.Record is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Rollout '{wanted}' not found for [cyan]{resolved.EnvName}[/].");
                return 1;
            }

            var record = match.Record;
            var operation = record.Operation.ToString().ToLowerInvariant();
            AnsiConsole.MarkupLineInterpolated($"[bold]{record.Id}[/] ({operation}) on {record.Target}, started {record.StartedAt.ToLocalTime():g}");
            AnsiConsole.MarkupLine($"State: {StateMarkup(record.State)}" +
                                   (record.Reason is null ? "" : $" — {Markup.Escape(record.Reason)}"));

            var services = new Table().AddColumns("Service", "State", "Before", "Deployed", "Restored", "Detail");
            foreach (var s in record.Services)
            {
                services.AddRow(
                    Markup.Escape(s.Name),
                    Markup.Escape(s.State.ToString()),
                    Markup.Escape(s.Snapshot is null ? "" : s.Snapshot.Existed ? s.Snapshot.Revision ?? "?" : "(new)"),
                    Markup.Escape(s.Revision ?? ""),
                    Markup.Escape(s.RestoredRevision ?? ""),
                    Markup.Escape(s.Detail ?? ""));
            }

            AnsiConsole.Write(services);
            foreach (var e in record.Events.Where(e => e.Service is null))
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]{e.At.ToLocalTime():T} {e.Message}[/]");
            }

            AnsiConsole.MarkupLineInterpolated($"[dim]Journal: {match.Path}[/]");
            return 0;
        });

        return cmd;
    }

    private static string StateMarkup(RolloutState state) => state switch
    {
        RolloutState.Succeeded => "[green]Succeeded[/]",
        RolloutState.RolledBack => "[yellow]RolledBack[/]",
        RolloutState.RollbackFailed => "[red]RollbackFailed[/]",
        RolloutState.Failed => "[red]Failed[/]",
        _ => state.ToString()
    };
}
