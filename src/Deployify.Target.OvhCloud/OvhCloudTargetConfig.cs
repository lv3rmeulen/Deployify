namespace Deployify.Target.OvhCloud;

/// <summary>
/// OVHcloud settings stored under targetConfig["ovhcloud"] in deployify-state.json. Namespaces,
/// ingress and resources come from targetConfig["kubernetes"], shared with the Kubernetes target.
/// </summary>
/// <remarks>
/// Credentials are best left out of the state file: each one falls back to the environment
/// variable the OVHcloud SDKs and Terraform provider read (OVH_APPLICATION_KEY, …), and a
/// configured value may be an <c>"env:NAME"</c> reference.
/// </remarks>
public sealed class OvhCloudTargetConfig
{
    /// <summary>
    /// API endpoint: <c>ovh-eu</c> (default), <c>ovh-ca</c> or <c>ovh-us</c>, or a full API URL.
    /// When unset, OVH_ENDPOINT is used.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Public Cloud project ID (the API's serviceName). When unset, OVH_CLOUD_PROJECT_SERVICE is used.</summary>
    public string? ProjectId { get; set; }

    /// <summary>ID of the Managed Kubernetes cluster in that project.</summary>
    public string? ClusterId { get; set; }

    /// <summary>Application key of an API application. When unset, OVH_APPLICATION_KEY is used.</summary>
    public string? ApplicationKey { get; set; }

    /// <summary>Application secret. Prefer <c>"env:NAME"</c>; when unset, OVH_APPLICATION_SECRET is used.</summary>
    public string? ApplicationSecret { get; set; }

    /// <summary>Consumer key. Prefer <c>"env:NAME"</c>; when unset, OVH_CONSUMER_KEY is used.</summary>
    public string? ConsumerKey { get; set; }

    /// <summary>
    /// Service account (OAuth2 client credentials) ID, used instead of the application keys when
    /// set together with <see cref="ClientSecret"/>. When unset, OVH_CLIENT_ID is used.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Service account secret. Prefer <c>"env:NAME"</c>; when unset, OVH_CLIENT_SECRET is used.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Where the kubeconfig fetched from the API is written. Default:
    /// <c>{LocalApplicationData}/deployify/ovhcloud/{projectId}-{clusterId}.kubeconfig</c>.
    /// </summary>
    public string? KubeconfigPath { get; set; }

    /// <summary>
    /// Wait for a cluster that is still being created, updated or resized before talking to it,
    /// instead of failing straight away.
    /// </summary>
    public bool WaitForReady { get; set; } = true;

    /// <summary>How long to wait for the cluster to become ready, in seconds.</summary>
    public int ReadyTimeoutSeconds { get; set; } = 600;
}
