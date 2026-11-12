using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Deployify.Core.Models;

namespace Deployify.Core.Deployments;

public enum ManifestDiagnosticSeverity
{
    /// <summary>Not deployed, and the user said that's fine (<c>ignoreResources</c>).</summary>
    Info,

    /// <summary>Not deployed, and nothing deployed depends on it. Blocks only with --strict.</summary>
    Warning,

    /// <summary>Not deployed, but a deployed service depends on it. Blocks deploying.</summary>
    Error
}

/// <summary>One finding about a manifest resource Deployify won't deploy, or about the <c>ignoreResources</c> list.</summary>
public sealed record ManifestDiagnostic(string Resource, string? Type, ManifestDiagnosticSeverity Severity, string Message);

/// <summary>What <see cref="ManifestAudit.Run"/> found.</summary>
public sealed class ManifestAuditResult
{
    public static readonly ManifestAuditResult Empty = new([], []);

    public ManifestAuditResult(IReadOnlyList<ManifestDiagnostic> diagnostics, IReadOnlyList<PlanSkipped> skipped)
    {
        Diagnostics = diagnostics;
        Skipped = skipped;
    }

    /// <summary>Findings, errors first, then by resource name.</summary>
    public IReadOnlyList<ManifestDiagnostic> Diagnostics { get; }

    /// <summary>Every resource in the manifest that won't be deployed although it may matter, with why.</summary>
    public IReadOnlyList<PlanSkipped> Skipped { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == ManifestDiagnosticSeverity.Error);

    public bool HasWarnings => Diagnostics.Any(d => d.Severity == ManifestDiagnosticSeverity.Warning);

    /// <summary>
    /// True when a deploy should stop: always on an error, and on a warning too when
    /// <paramref name="strict"/> is set.
    /// </summary>
    public bool Blocks(bool strict) => HasErrors || (strict && HasWarnings);

    /// <summary>The line a deploy stops with when <see cref="Blocks"/> is true.</summary>
    public string BlockedSummary(bool strict)
    {
        var errors = Diagnostics.Count(d => d.Severity == ManifestDiagnosticSeverity.Error);
        var warnings = Diagnostics.Count(d => d.Severity == ManifestDiagnosticSeverity.Warning);
        return errors > 0
            ? $"{errors} resource(s) that deployed services depend on can't be deployed; nothing was changed."
            : $"{warnings} manifest resource(s) won't be deployed and --strict is set; nothing was changed.";
    }
}

/// <summary>
/// Checks, before anything is planned, that every resource in the manifest is either deployed or
/// deliberately not deployed. Targets deploy workloads only (<see cref="AspireResource.IsDeployable"/>);
/// without this check, a resource of any other type would be dropped without a word and the app
/// would be deployed with a piece missing.
/// </summary>
public static class ManifestAudit
{
    /// <param name="manifest">The manifest as targets will see it (dashboard, Dapr and references applied).</param>
    /// <param name="ignoreResources">The environment's <c>ignoreResources</c>: resources the user knows won't be deployed.</param>
    /// <param name="handledByTarget">Resource types the target deploys itself (<see cref="Interfaces.IDeploymentTarget.AdditionalResourceTypes"/>).</param>
    public static ManifestAuditResult Run(
        AspireManifest manifest,
        IReadOnlyCollection<string>? ignoreResources = null,
        IReadOnlyCollection<string>? handledByTarget = null)
    {
        var ignored = new HashSet<string>(ignoreResources ?? [], StringComparer.Ordinal);
        var handled = new HashSet<string>(handledByTarget ?? [], StringComparer.Ordinal);
        var dependents = Dependents(manifest);
        var diagnostics = new List<ManifestDiagnostic>();
        var skipped = new List<PlanSkipped>();

        foreach (var name in manifest.Resources.Keys.Order(StringComparer.Ordinal))
        {
            var resource = manifest.Resources[name];
            var classification = ResourceTypes.Classify(name, manifest);

            if (ignored.Contains(name) && !classification.IsGap)
            {
                diagnostics.Add(new(name, resource.Type, ManifestDiagnosticSeverity.Warning,
                    $"'{name}' is listed under ignoreResources, but Deployify handles it already; remove it from the list."));
                continue;
            }

            if (!classification.IsGap || handled.Contains(resource.Type))
            {
                continue;
            }

            var reason = classification.Reason!;
            skipped.Add(new PlanSkipped(name, resource.Type, reason));
            var users = dependents.TryGetValue(name, out var u) ? u : [];
            var usedBy = users.Count > 0
                ? $"{string.Join(", ", users.Select(w => $"'{w}'"))} depend{(users.Count == 1 ? "s" : "")} on it"
                : null;

            if (ignored.Contains(name))
            {
                diagnostics.Add(new(name, resource.Type, ManifestDiagnosticSeverity.Info,
                    $"'{name}' ({resource.Type}) is not deployed (ignoreResources)" +
                    (usedBy is null ? "." : $"; {usedBy}, so make sure they get it another way.")));
            }
            else if (usedBy is not null)
            {
                diagnostics.Add(new(name, resource.Type, ManifestDiagnosticSeverity.Error,
                    $"'{name}' {reason}. {usedBy} and would be deployed without it. " +
                    "Fix that, or list it under ignoreResources to deploy without it."));
            }
            else
            {
                diagnostics.Add(new(name, resource.Type, ManifestDiagnosticSeverity.Warning,
                    $"'{name}' {reason}. It won't be deployed. Nothing deployed depends on it; " +
                    "list it under ignoreResources if that's intended."));
            }
        }

        foreach (var name in ignored.Where(n => !manifest.Resources.ContainsKey(n)).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(new(name, null, ManifestDiagnosticSeverity.Warning,
                $"ignoreResources lists '{name}', which isn't in the manifest; remove it from the list."));
        }

        return new ManifestAuditResult(
            [.. diagnostics.OrderByDescending(d => d.Severity).ThenBy(d => d.Resource, StringComparer.Ordinal)],
            skipped);
    }

    /// <summary>Writes the findings to <paramref name="reporter"/>: errors, warnings and info lines.</summary>
    public static void Report(ManifestAuditResult result, IDeploymentReporter reporter)
    {
        foreach (var d in result.Diagnostics)
        {
            switch (d.Severity)
            {
                case ManifestDiagnosticSeverity.Error:
                    reporter.Error(d.Message);
                    break;
                case ManifestDiagnosticSeverity.Warning:
                    reporter.Warn(d.Message);
                    break;
                default:
                    reporter.Info(d.Message);
                    break;
            }
        }
    }

    /// <summary>
    /// For each resource, the workloads that depend on it: directly through a
    /// <c>{name.…}</c> placeholder in their env, args or connection string, or through inputs
    /// (values, parameters, child resources) that lead to it.
    /// </summary>
    private static Dictionary<string, SortedSet<string>> Dependents(AspireManifest manifest)
    {
        var resources = manifest.Resources;
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (workload, resource) in resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { workload };
            var pending = new Stack<string>(AspireManifestExtensions.PlaceholderSources(resource));
            while (pending.TryPop(out var text))
            {
                foreach (Match m in AspireManifestExtensions.s_placeholderRef.Matches(text))
                {
                    Visit(m.Groups["res"].Value);
                }
            }

            void Visit(string name)
            {
                if (!seen.Add(name) || !resources.TryGetValue(name, out var target))
                {
                    return;
                }

                if (!result.TryGetValue(name, out var users))
                {
                    result[name] = users = new SortedSet<string>(StringComparer.Ordinal);
                }

                users.Add(workload);

                // A workload is a dependency in its own right; what it depends on is its own business.
                if (target.IsDeployable)
                {
                    return;
                }

                if (target.Parent is not null)
                {
                    Visit(target.Parent);
                }

                foreach (var text in AspireManifestExtensions.PlaceholderSources(target))
                {
                    pending.Push(text);
                }

                if (target.ExtensionData is not null &&
                    target.ExtensionData.TryGetValue("value", out var v) &&
                    v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    pending.Push(v.GetString()!);
                }
            }
        }

        return result;
    }
}
