using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Target.Kubernetes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.GitOps;

/// <summary>
/// GitOps with Flux: one GitRepository for the environment and a Kustomization per service,
/// named like its namespace, in targetConfig["flux"].namespace. <c>gitops.wait</c> watches the
/// Kustomization's last applied revision and Ready condition.
/// </summary>
public sealed class FluxDeploymentTarget : GitOpsDeploymentTarget
{
    public FluxDeploymentTarget(IPublisher publisher, ILoggerFactory loggers)
        : this(publisher, loggers, ProcessRunner.Default)
    {
    }

    internal FluxDeploymentTarget(IPublisher publisher, ILoggerFactory loggers, IProcessRunner processes)
        : base(publisher, loggers, processes)
    {
    }

    public override DeploymentTargetType TargetType => DeploymentTargetType.Flux;

    public override string DisplayName => "Flux (GitOps)";

    protected override string ControllerName => "Flux";

    protected override string ControllerCrd => FluxManifests.Crd;

    protected override string RegistrationResource => FluxManifests.KustomizationResource;

    protected override string RegistrationKind => "Kustomization";

    internal static FluxTargetConfig GetFluxConfig(EnvironmentConfig config) => Read<FluxTargetConfig>(config, "flux");

    protected override string RegistrationNamespace(EnvironmentConfig config) => GetFluxConfig(config).Namespace;

    protected override string RenderServiceRegistration(KubernetesServiceManifest svc, EnvironmentConfig config, string repoPath)
        => FluxManifests.Kustomization(svc, GetFluxConfig(config), KubernetesConfig(config), repoPath);

    /// <summary>The GitRepository, unless an existing one is used (<c>flux.createSource</c> off).</summary>
    protected override string? RenderEnvironmentRegistration(EnvironmentConfig config)
    {
        var flux = GetFluxConfig(config);
        return flux.CreateSource ? FluxManifests.GitRepository(GetGitOpsConfig(config), flux, KubernetesConfig(config)) : null;
    }

    protected override HealthObservation Evaluate(string? json, string commit) => FluxManifests.Evaluate(json, commit);

    /// <summary>Asks source-controller to fetch the new commit, then each Kustomization to apply it.</summary>
    protected override async Task RefreshAsync(
        ControllerClient client, EnvironmentConfig config, IReadOnlyList<string> registrationNames, CancellationToken ct)
    {
        var flux = GetFluxConfig(config);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var source = FluxManifests.SourceName(flux, KubernetesConfig(config));
        if (!await client.AnnotateAsync(FluxManifests.GitRepositoryResource, flux.Namespace, source, FluxManifests.ReconcileAnnotation, now, ct))
        {
            Logger.LogDebug("Could not ask Flux to fetch {Source}; it will see the commit within {Interval}.", source, flux.Interval);
        }

        foreach (var name in registrationNames)
        {
            if (!await client.AnnotateAsync(FluxManifests.KustomizationResource, flux.Namespace, name, FluxManifests.ReconcileAnnotation, now, ct))
            {
                Logger.LogDebug("Could not ask Flux to reconcile {Name}.", name);
            }
        }
    }
}
