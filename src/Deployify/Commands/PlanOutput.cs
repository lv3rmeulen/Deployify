using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>Plan output shared by plan, apply, ci and watch, so they report the same thing.</summary>
internal static class PlanOutput
{
    /// <summary>
    /// "Plan: 1 to create, 0 to update, 0 to delete[, 2 could not be checked][, 1 not deployed]."
    /// as markup.
    /// </summary>
    public static string Summary(PlanResult plan, string prefix = "Plan:")
    {
        var line = $"{prefix} [green]{plan.CreateCount} to create[/], [yellow]{plan.UpdateCount} to update[/], " +
                   $"[red]{plan.DeleteCount} to delete[/]";
        if (plan.UnknownCount > 0)
        {
            line += $", [blue]{plan.UnknownCount} could not be checked[/]";
        }

        if (plan.Skipped.Count > 0)
        {
            line += $", [grey]{plan.Skipped.Count} not deployed ({Markup.Escape(string.Join(", ", plan.Skipped.Select(s => s.Name)))})[/]";
        }

        return line + ".";
    }

    /// <summary>
    /// True, after saying why, when the manifest has resources Deployify won't deploy that a
    /// deployed service depends on (or any, with --strict). The findings themselves were
    /// reported when the manifest was loaded.
    /// </summary>
    public static bool AuditBlocks(ResolvedContext resolved, bool strict)
    {
        if (!resolved.ManifestAudit.Blocks(strict))
        {
            return false;
        }

        AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {resolved.ManifestAudit.BlockedSummary(strict)}");
        return true;
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

    /// <summary>
    /// True, after listing them, when --require-digest is set and a built image would be
    /// deployed by tag because no published digest is recorded for it.
    /// </summary>
    public static bool UnpinnedBlocks(AspireManifest manifest, EnvironmentConfig config, bool requireDigest)
    {
        if (!requireDigest)
        {
            return false;
        }

        var unpinned = Core.Deployments.ImagePromotion.Unpinned(manifest, config);
        if (unpinned.Count == 0)
        {
            return false;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[red]✗[/] --require-digest is set, but {unpinned.Count} image(s) have no published digest for tag {config.ImageTag}:");
        foreach (var (service, reference) in unpinned)
        {
            AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {service}: {reference}");
        }

        AnsiConsole.MarkupLine("Publish them to this environment, or promote them with [cyan]deployify promote[/].");
        return true;
    }
}
