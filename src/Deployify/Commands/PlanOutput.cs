using System.Collections.Generic;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>Plan output shared by plan, apply, ci and watch, so they report the same thing.</summary>
internal static class PlanOutput
{
    /// <summary>"Plan: 1 to create, 0 to update, 0 to delete[, 2 could not be checked]." as markup.</summary>
    public static string Summary(PlanResult plan, string prefix = "Plan:")
    {
        var line = $"{prefix} [green]{plan.CreateCount} to create[/], [yellow]{plan.UpdateCount} to update[/], " +
                   $"[red]{plan.DeleteCount} to delete[/]";
        return plan.UnknownCount > 0
            ? $"{line}, [blue]{plan.UnknownCount} could not be checked[/]."
            : line + ".";
    }

    /// <summary>The "could not be checked" section: each service with the reason.</summary>
    public static void PrintUnknown(PlanResult plan)
    {
        if (plan.UnknownCount == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[blue]? Could not be checked[/]");
        foreach (var u in plan.Unknown)
        {
            AnsiConsole.MarkupLineInterpolated($"  [bold blue]{u.ServiceName}[/] [dim]{u.Reason}[/]");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>"+ 2 to create: api, web" with the names escaped.</summary>
    public static void PrintList(string color, string symbol, string verb, IReadOnlyCollection<string> names)
    {
        if (names.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[{color}]{symbol} {names.Count} {verb}[/]: {string.Join(", ", names)}");
        }
    }
}
