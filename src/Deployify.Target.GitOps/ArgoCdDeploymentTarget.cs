using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.GitOps;

/// <summary>
/// GitOps with Argo CD: each service's folder is an Argo CD Application named like its
/// namespace, in targetConfig["argocd"].namespace. Argo CD syncs it (automatically by default)
/// and reports sync and health status, which <c>gitops.wait</c> watches.
/// </summary>
public sealed class ArgoCdDeploymentTarget : GitOpsDeploymentTarget
{
    public ArgoCdDeploymentTarget(IPublisher publisher, ILoggerFactory loggers)
        : this(publisher, loggers, ProcessRunner.Default)
    {
    }

    internal ArgoCdDeploymentTarget(IPublisher publisher, ILoggerFactory loggers, IProcessRunner processes)
        : base(publisher, loggers, processes)
    {
    }

    public override DeploymentTargetType TargetType => DeploymentTargetType.ArgoCd;

    public override string DisplayName => "Argo CD (GitOps)";

    protected override string ControllerName => "Argo CD";

    protected override string ControllerCrd => ArgoCdManifests.Crd;

    protected override string RegistrationResource => ArgoCdManifests.Resource;

    protected override string RegistrationKind => "Application";

    internal static ArgoCdTargetConfig GetArgoCdConfig(EnvironmentConfig config) => Read<ArgoCdTargetConfig>(config, "argocd");

    protected override string RegistrationNamespace(EnvironmentConfig config) => GetArgoCdConfig(config).Namespace;

    protected override string RenderServiceRegistration(KubernetesServiceManifest svc, EnvironmentConfig config, string repoPath)
        => ArgoCdManifests.Application(svc, GetGitOpsConfig(config), GetArgoCdConfig(config), repoPath);

    /// <summary>Argo CD reads the repository itself (with credentials configured in Argo CD); nothing is shared.</summary>
    protected override string? RenderEnvironmentRegistration(EnvironmentConfig config) => null;

    protected override HealthObservation Evaluate(string? json, string commit) => ArgoCdManifests.Evaluate(json, commit);

    protected override async Task RefreshAsync(
        ControllerClient client, EnvironmentConfig config, IReadOnlyList<string> registrationNames, CancellationToken ct)
    {
        var ns = RegistrationNamespace(config);
        foreach (var name in registrationNames)
        {
            if (!await client.AnnotateAsync(ArgoCdManifests.Resource, ns, name, ArgoCdManifests.RefreshAnnotation, "normal", ct))
            {
                Logger.LogDebug("Could not ask Argo CD to refresh {Name}; it will see the commit at its next poll.", name);
            }
        }
    }
}
