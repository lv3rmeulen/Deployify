namespace Deployify.Core.Models;

public enum DeploymentTargetType
{
    Nomad,
    Kubernetes,
    Compose,
    AwsEcs,
    AzureContainerApps,
    NomadConsul
}
