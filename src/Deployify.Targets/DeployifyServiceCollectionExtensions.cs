using Deployify.Core.Interfaces;
using Deployify.Core.SecretBackends;
using Deployify.Plugins;
using Deployify.Core.Services;
using Deployify.Target.AwsEcs;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.Compose;
using Deployify.Target.GitOps;
using Deployify.Target.Helm;
using Deployify.Target.Iis;
using Deployify.Target.Kubernetes;
using Deployify.Target.Nomad;
using Deployify.Target.NomadConsul;
using Deployify.Target.OvhCloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deployify.Targets;

public static class DeployifyServiceCollectionExtensions
{
    /// <summary>
    /// Core services, every built-in target, and plugin targets found on demand (see
    /// <see cref="PluginCatalog"/>). Logging is up to the host: the CLI adds a console logger,
    /// the Aspire integration hands in the AppHost's logger factory.
    /// </summary>
    public static IServiceCollection AddDeployify(this IServiceCollection services)
        => services
            .AddSingleton<SecretRegistry>()
            .AddSingleton<IStateManager>(sp => new StateManager(sp.GetRequiredService<SecretRegistry>()))
            .AddSingleton<IManifestReader, ManifestReader>()
            .AddSingleton<JobGenerator>()
            .AddSingleton<IPublisher, Publisher>()
            .AddSingleton<IDeploymentTarget, NomadDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, KubernetesDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, ComposeDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, EcsDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, AcaDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, NomadConsulDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, HelmDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, ArgoCdDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, FluxDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, OvhCloudDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, IisDeploymentTarget>()
            .AddSingleton(_ => new PluginCatalog())
            .AddSingleton(sp => new PluginTargetSource(
                sp.GetRequiredService<PluginCatalog>(), sp.GetService<ILoggerFactory>()))
            .AddSingleton<IDeploymentTargetSource>(sp => sp.GetRequiredService<PluginTargetSource>())
            .AddSingleton<IDeploymentTargetFactory>(sp =>
                new DeploymentTargetFactory(sp.GetServices<IDeploymentTarget>(), sp.GetServices<IDeploymentTargetSource>()));
}
