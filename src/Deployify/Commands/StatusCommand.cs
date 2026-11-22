using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Deployify.Commands.Json;
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
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(rolloutId);
        cmd.Options.Add(limit);

        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "status", async json =>
        {
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(
                    parseResult.GetValue(GlobalOptions.ProjectPath)!,
                    parseResult.GetValue(GlobalOptions.Environment),
                    parseResult.GetValue(GlobalOptions.Target), ct,
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            json?.Use(resolved);
            var rollouts = await RolloutJournal.ListAsync(resolved.Directory, resolved.EnvName, ct);
            var wanted = parseResult.GetValue(rolloutId);

            if (wanted is null)
            {
                var shown = rollouts.Take(Math.Max(1, parseResult.GetValue(limit))).Select(r => r.Record).ToList();
                if (json is not null)
                {
                    json.Result = new JsonStatus([.. shown.Select(ToJsonSummary)], null);
                }

                if (rollouts.Count == 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"No rollouts recorded for [cyan]{resolved.EnvName}[/].");
                    return 0;
                }

                var list = new Table().AddColumns("Rollout", "Operation", "Started", "State", "Reason");
                foreach (var r in shown)
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
                ConsoleDeploymentReporter.Instance.Error($"Rollout '{wanted}' not found for {resolved.EnvName}.");
                return 1;
            }

            var record = match.Record;
            if (json is not null)
            {
                json.Result = new JsonStatus(null, ToJson(record, match.Path));
            }

            var operation = record.Operation.ToString().ToLowerInvariant();
            AnsiConsole.MarkupLineInterpolated($"[bold]{record.Id}[/] ({operation}) on {record.Target}, started {record.StartedAt.ToLocalTime():g}");
            AnsiConsole.MarkupLine($"State: {StateMarkup(record.State)}" +
                                   (record.Reason is null ? "" : $" — {Markup.Escape(record.Reason)}"));

            var progressive = record.Services.Any(s => s.Strategy is not null);
            var services = new Table().AddColumns("Service", "State", "Before", "Deployed", "Restored", "Detail");
            if (progressive)
            {
                services.AddColumn("Strategy");
            }

            foreach (var s in record.Services)
            {
                string[] cells =
                [
                    Markup.Escape(s.Name),
                    Markup.Escape(s.State.ToString()),
                    Markup.Escape(s.Snapshot is null ? "" : s.Snapshot.Existed ? s.Snapshot.Revision ?? "?" : "(new)"),
                    Markup.Escape(s.Revision ?? ""),
                    Markup.Escape(s.RestoredRevision ?? ""),
                    Markup.Escape(s.Detail ?? "")
                ];
                services.AddRow(progressive
                    ? [.. cells, Markup.Escape(s.Strategy is null ? "" : $"{s.Strategy}{(s.TrafficPercent is { } p ? $" (at {p}%)" : "")}")]
                    : cells);
            }

            AnsiConsole.Write(services);
            foreach (var e in record.Events.Where(e => e.Service is null))
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]{e.At.ToLocalTime():T} {e.Message}[/]");
            }

            AnsiConsole.MarkupLineInterpolated($"[dim]Journal: {match.Path}[/]");
            return 0;
        }));

        return cmd;
    }

    internal static JsonRolloutSummary ToJsonSummary(RolloutRecord r) => new(
        r.Id, JsonOutput.Name(r.Operation), r.Target, r.StartedAt, r.FinishedAt, JsonOutput.Name(r.State), r.Reason);

    /// <summary>The rollout <c>status --rollout ID --output json</c> shows, with all of its events.</summary>
    internal static JsonRollout ToJson(RolloutRecord r, string journalPath) => new(
        r.Id, JsonOutput.Name(r.Operation), r.Target, r.StartedAt, r.FinishedAt, JsonOutput.Name(r.State), r.Reason,
        [.. r.Waves.Select(w => (IReadOnlyList<string>)[.. w])],
        [.. r.Services.Select(s => new JsonRolloutService(
            s.Name,
            JsonOutput.Name(s.State),
            s.Snapshot is null ? null : new JsonServiceSnapshot(s.Snapshot.Existed, s.Snapshot.Revision),
            s.Revision,
            s.RestoredRevision,
            s.Detail,
            s.Strategy,
            s.Candidate,
            s.TrafficPercent,
            s.PreviewUrl))],
        [.. r.Events.Select(e => new JsonRolloutEvent(e.At, e.Service, e.Message))],
        journalPath);

    private static string StateMarkup(RolloutState state) => state switch
    {
        RolloutState.Succeeded => "[green]Succeeded[/]",
        RolloutState.RolledBack => "[yellow]RolledBack[/]",
        RolloutState.RollbackFailed => "[red]RollbackFailed[/]",
        RolloutState.Failed => "[red]Failed[/]",
        _ => state.ToString()
    };
}
