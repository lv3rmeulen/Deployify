namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// Azure Container Apps-specific settings stored under targetConfig["aca"]
/// in deployify-state.json.
/// </summary>
public sealed class AcaTargetConfig
{
    /// <summary>Azure subscription ID.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Azure resource group name. Created if it does not exist.</summary>
    public string ResourceGroup { get; set; } = "deployify-rg";

    /// <summary>Azure region, e.g. "eastus", "westeurope".</summary>
    public string Location { get; set; } = "eastus";

    /// <summary>
    /// Container Apps Environment name.
    /// All apps in this deployment share one environment (and therefore one VNet injection).
    /// Created automatically if it does not exist.
    /// </summary>
    public string EnvironmentName { get; set; } = "deployify-env";

    /// <summary>
    /// Log Analytics workspace name used by the Container Apps environment.
    /// Created automatically alongside the environment if it does not exist.
    /// </summary>
    public string LogAnalyticsWorkspaceName { get; set; } = "deployify-logs";

    /// <summary>Default minimum number of replicas. 0 = scale to zero.</summary>
    public int MinReplicas { get; set; } = 0;

    /// <summary>Default maximum number of replicas.</summary>
    public int MaxReplicas { get; set; } = 3;

    /// <summary>Default CPU allocation in cores (e.g. 0.25, 0.5, 1.0).</summary>
    public double DefaultCpu { get; set; } = 0.25;

    /// <summary>Default memory allocation in Gi (e.g. "0.5Gi", "1.0Gi").</summary>
    public string DefaultMemory { get; set; } = "0.5Gi";

    /// <summary>
    /// Whether to generate ingress for services with HTTP bindings.
    /// External = publicly reachable; Internal = VNet-only.
    /// </summary>
    public AcaIngressType IngressType { get; set; } = AcaIngressType.External;

    /// <summary>
    /// Azure Container Registry login server, e.g. myregistry.azurecr.io.
    /// Used to configure ACR pull credentials on the environment.
    /// </summary>
    public string? AcrLoginServer { get; set; }

    /// <summary>Azure CLI profile / tenant to use. Null = currently logged-in account.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Resource ID of a user-assigned managed identity that has AcrPull on the registry.
    /// Recommended: a system-assigned identity only exists after the app is created, so it
    /// can't be granted AcrPull in time for the first image pull.
    /// </summary>
    public string? AcrIdentityResourceId { get; set; }

    /// <summary>Base port for services whose Aspire bindings carry no port.</summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>
    /// The Deployify environment this config belongs to. Not part of the config file: filled in
    /// from the environment's name, and written to every app as the deployify-env tag.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DeployifyEnvironment { get; set; }
}

public enum AcaIngressType
{
    External,
    Internal,
    None
}
