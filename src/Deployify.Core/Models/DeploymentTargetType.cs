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
    Helm
}
