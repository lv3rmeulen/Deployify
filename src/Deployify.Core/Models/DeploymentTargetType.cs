namespace Deployify.Core.Models;

public enum DeploymentTargetType
{
    Nomad,
    Kubernetes,
    Compose,
    AwsEcs,
    AzureContainerApps,
    NomadConsul,

    /// <summary>Kubernetes, with each service installed as a Helm release.</summary>
    Helm,

    /// <summary>Kubernetes through GitOps: manifests committed to a repository, synced by Argo CD.</summary>
    ArgoCd,

    /// <summary>Kubernetes through GitOps: manifests committed to a repository, applied by Flux.</summary>
    Flux
}
