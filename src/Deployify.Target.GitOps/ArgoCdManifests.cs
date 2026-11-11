using System;
using System.Text;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Target.Kubernetes;

namespace Deployify.Target.GitOps;

/// <summary>The Argo CD Application for a service, and how to read its sync and health status.</summary>
public static class ArgoCdManifests
{
    public const string Resource = "applications.argoproj.io";
    public const string Crd = "applications.argoproj.io";

    /// <summary>Annotation that makes Argo CD look at the repository now instead of at its next poll.</summary>
    public const string RefreshAnnotation = "argocd.argoproj.io/refresh";

    /// <summary>
    /// One Application per service, named like its namespace. The finalizer makes deleting the
    /// Application delete what it deployed, which is how destroy and prune remove a service.
    /// </summary>
    public static string Application(
        KubernetesServiceManifest svc, GitOpsTargetConfig gitops, ArgoCdTargetConfig argo, string repoPath)
    {
        var q = (Func<string?, string>)KubernetesManifestGenerator.QuoteYaml;
        var sb = new StringBuilder();
        sb.Append($"""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: {svc.Namespace}
              namespace: {argo.Namespace}
              labels:
                app.kubernetes.io/managed-by: deployify
                deployify/service: {svc.ServiceName}
              finalizers:
                - resources-finalizer.argocd.argoproj.io
            spec:
              project: {q(argo.Project)}
              source:
                repoURL: {q(gitops.RepoUrl)}
                targetRevision: {q(gitops.Branch)}
                path: {q($"{repoPath}/{svc.ServiceName}")}
              destination:
                server: {q(argo.DestinationServer)}
                namespace: {svc.Namespace}
              syncPolicy:

            """);
        if (argo.AutoSync)
        {
            sb.Append($"""
                    automated:
                      prune: {Bool(argo.Prune)}
                      selfHeal: {Bool(argo.SelfHeal)}

                """);
        }

        sb.Append("""
                syncOptions:
                  - PruneLast=true

            """);
        return sb.ToString();
    }

    /// <summary>
    /// Healthy once Argo CD has synced <paramref name="commit"/> and the Application is Healthy;
    /// Failed when the sync operation failed or the app is Degraded at that commit.
    /// </summary>
    public static HealthObservation Evaluate(string? json, string commit)
    {
        if (json is null)
        {
            return HealthObservation.Progressing("the Application doesn't exist yet");
        }

        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s : default;
        var revision = Get(status, "sync", "revision");
        var sync = Get(status, "sync", "status") ?? "Unknown";
        var health = Get(status, "health", "status") ?? "Unknown";
        var phase = Get(status, "operationState", "phase");
        var operationRevision = Get(status, "operationState", "syncResult", "revision");

        if (phase is "Failed" or "Error" && SameCommit(operationRevision, commit))
        {
            return HealthObservation.Failed($"sync {phase.ToLowerInvariant()}: {Get(status, "operationState", "message") ?? "no message"}");
        }

        if (!SameCommit(revision, commit))
        {
            return HealthObservation.Progressing($"Argo CD is at {Short(revision)}, waiting for {Short(commit)}");
        }

        if (health == "Degraded")
        {
            return HealthObservation.Failed($"degraded: {Get(status, "health", "message") ?? "see Argo CD"}");
        }

        return sync == "Synced" && health == "Healthy"
            ? HealthObservation.Healthy($"synced at {Short(commit)}")
            : HealthObservation.Progressing($"{sync}, {health}");
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>True when <paramref name="revision"/> names <paramref name="commit"/>, in full or abbreviated (7+ characters).</summary>
    internal static bool SameCommit(string? revision, string commit)
    {
        if (revision is null || revision.Length < 7)
        {
            return false;
        }

        return commit.StartsWith(revision, StringComparison.OrdinalIgnoreCase) ||
               revision.StartsWith(commit, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Short(string? revision) => revision is null ? "nothing" : revision.Length > 7 ? revision[..7] : revision;

    internal static string? Get(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
