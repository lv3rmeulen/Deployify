using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Target.Kubernetes;

namespace Deployify.Target.GitOps;

/// <summary>The Flux GitRepository and per-service Kustomizations, and how to read their status.</summary>
public static class FluxManifests
{
    public const string KustomizationResource = "kustomizations.kustomize.toolkit.fluxcd.io";
    public const string GitRepositoryResource = "gitrepositories.source.toolkit.fluxcd.io";
    public const string Crd = "kustomizations.kustomize.toolkit.fluxcd.io";

    /// <summary>Annotation that makes a Flux controller reconcile now instead of at its next interval.</summary>
    public const string ReconcileAnnotation = "reconcile.fluxcd.io/requestedAt";

    /// <summary>Failure reasons of a Kustomization's Ready condition, as opposed to "still progressing".</summary>
    private static readonly string[] s_failureReasons =
        ["ReconciliationFailed", "HealthCheckFailed", "BuildFailed", "ArtifactFailed", "DependencyNotReady"];

    public static string SourceName(FluxTargetConfig flux, KubernetesTargetConfig kubernetes)
        => string.IsNullOrWhiteSpace(flux.SourceName) ? kubernetes.NamespacePrefix : flux.SourceName;

    /// <summary>The GitRepository every Kustomization of the environment reads from.</summary>
    public static string GitRepository(GitOpsTargetConfig gitops, FluxTargetConfig flux, KubernetesTargetConfig kubernetes)
    {
        var q = (Func<string?, string>)KubernetesManifestGenerator.QuoteYaml;
        var sb = new StringBuilder();
        sb.Append($"""
            apiVersion: source.toolkit.fluxcd.io/v1
            kind: GitRepository
            metadata:
              name: {SourceName(flux, kubernetes)}
              namespace: {flux.Namespace}
              labels:
                app.kubernetes.io/managed-by: deployify
            spec:
              interval: {q(flux.Interval)}
              url: {q(gitops.RepoUrl)}
              ref:
                branch: {q(gitops.Branch)}

            """);
        if (!string.IsNullOrWhiteSpace(flux.SecretRef))
        {
            sb.Append($"""
                  secretRef:
                    name: {flux.SecretRef}

                """);
        }

        return sb.ToString();
    }

    /// <summary>
    /// One Kustomization per service, named like its namespace. With prune on, deleting it
    /// removes what it applied, which is how destroy and prune remove a service.
    /// </summary>
    public static string Kustomization(
        KubernetesServiceManifest svc, FluxTargetConfig flux, KubernetesTargetConfig kubernetes, string repoPath)
    {
        var q = (Func<string?, string>)KubernetesManifestGenerator.QuoteYaml;
        return $"""
            apiVersion: kustomize.toolkit.fluxcd.io/v1
            kind: Kustomization
            metadata:
              name: {svc.Namespace}
              namespace: {flux.Namespace}
              labels:
                app.kubernetes.io/managed-by: deployify
                deployify/service: {svc.ServiceName}
            spec:
              interval: {q(flux.Interval)}
              timeout: {q(flux.Timeout)}
              path: {q($"./{repoPath}/{svc.ServiceName}")}
              prune: {(flux.Prune ? "true" : "false")}
              wait: true
              sourceRef:
                kind: GitRepository
                name: {SourceName(flux, kubernetes)}

            """;
    }

    /// <summary>
    /// Healthy once the Kustomization's last applied revision is <paramref name="commit"/> and it
    /// is Ready; Failed when Ready is False for a failure reason while attempting that commit.
    /// Flux writes revisions as <c>main@sha1:abc…</c>.
    /// </summary>
    public static HealthObservation Evaluate(string? json, string commit)
    {
        if (json is null)
        {
            return HealthObservation.Progressing("the Kustomization doesn't exist yet");
        }

        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s : default;
        var applied = Commit(ArgoCdManifests.Get(status, "lastAppliedRevision"));
        var attempted = Commit(ArgoCdManifests.Get(status, "lastAttemptedRevision"));
        var (ready, reason, message) = ReadyCondition(status);

        if (ready == "False" && reason is not null && s_failureReasons.Contains(reason) && ArgoCdManifests.SameCommit(attempted, commit))
        {
            return HealthObservation.Failed($"{reason}: {message ?? "see the Kustomization's events"}");
        }

        if (!ArgoCdManifests.SameCommit(applied, commit))
        {
            return HealthObservation.Progressing(
                $"Flux is at {ArgoCdManifests.Short(applied)}, waiting for {ArgoCdManifests.Short(commit)}");
        }

        return ready == "True"
            ? HealthObservation.Healthy($"applied {ArgoCdManifests.Short(commit)}")
            : HealthObservation.Progressing(message ?? reason ?? "not ready yet");
    }

    /// <summary>The commit from a Flux revision (<c>main@sha1:abc…</c>, or a bare commit from older versions).</summary>
    internal static string? Commit(string? revision)
    {
        if (revision is null)
        {
            return null;
        }

        var colon = revision.LastIndexOf(':');
        if (colon >= 0)
        {
            return revision[(colon + 1)..];
        }

        var slash = revision.LastIndexOf('/');
        return slash >= 0 ? revision[(slash + 1)..] : revision;
    }

    private static (string? Status, string? Reason, string? Message) ReadyCondition(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }

        foreach (var c in conditions.EnumerateArray())
        {
            if (ArgoCdManifests.Get(c, "type") == "Ready")
            {
                return (ArgoCdManifests.Get(c, "status"), ArgoCdManifests.Get(c, "reason"), ArgoCdManifests.Get(c, "message"));
            }
        }

        return (null, null, null);
    }
}
