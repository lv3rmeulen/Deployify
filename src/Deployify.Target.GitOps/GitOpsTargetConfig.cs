namespace Deployify.Target.GitOps;

/// <summary>
/// Settings shared by the Argo CD and Flux targets, stored under targetConfig["gitops"] in
/// deployify-state.json. The manifests themselves come from targetConfig["kubernetes"], shared
/// with the Kubernetes and Helm targets.
/// </summary>
public sealed class GitOpsTargetConfig
{
    /// <summary>The config repository the controller watches (https or ssh URL). Required.</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>Branch Deployify commits to and the controller follows.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Folder in the repository that holds this environment, with forward slashes. Each service
    /// gets a folder under it. Null means <c>deployify/{environment}</c>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Local clone Deployify works in. Null means a folder per environment and repository under
    /// the system temp folder. It is Deployify's own: anything uncommitted in it is thrown away
    /// on every run.
    /// </summary>
    public string? CheckoutDirectory { get; set; }

    /// <summary>Commit author name.</summary>
    public string AuthorName { get; set; } = "Deployify";

    /// <summary>Commit author email.</summary>
    public string AuthorEmail { get; set; } = "deployify@localhost";

    /// <summary>Push the commit. Off: apply only commits, for a pipeline that pushes or opens a pull request itself.</summary>
    public bool Push { get; set; } = true;

    /// <summary>
    /// Apply the controller's objects (Argo CD Applications, Flux Kustomizations and
    /// GitRepository) to the cluster with kubectl. Off when they are managed elsewhere, for
    /// example from the same repository with an app-of-apps; they are committed either way.
    /// </summary>
    public bool Register { get; set; } = true;

    /// <summary>After pushing, wait until the controller has applied the commit and the services are healthy.</summary>
    public bool Wait { get; set; }

    /// <summary>How long <see cref="Wait"/> waits, in seconds.</summary>
    public int WaitTimeoutSeconds { get; set; } = 300;
}

/// <summary>Argo CD settings, stored under targetConfig["argocd"].</summary>
public sealed class ArgoCdTargetConfig
{
    /// <summary>Namespace Argo CD runs in; the Applications are created there.</summary>
    public string Namespace { get; set; } = "argocd";

    /// <summary>The Argo CD project the Applications belong to.</summary>
    public string Project { get; set; } = "default";

    /// <summary>API server of the cluster the services are deployed to.</summary>
    public string DestinationServer { get; set; } = "https://kubernetes.default.svc";

    /// <summary>Let Argo CD sync on its own (<c>syncPolicy.automated</c>). Off: sync from the Argo CD UI or CLI.</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>Delete objects that are no longer in the repository.</summary>
    public bool Prune { get; set; } = true;

    /// <summary>Revert changes made to the cluster by hand.</summary>
    public bool SelfHeal { get; set; } = true;
}

/// <summary>Flux settings, stored under targetConfig["flux"].</summary>
public sealed class FluxTargetConfig
{
    /// <summary>Namespace Flux runs in; the GitRepository and Kustomizations are created there.</summary>
    public string Namespace { get; set; } = "flux-system";

    /// <summary>Name of the GitRepository source. Null means the namespace prefix.</summary>
    public string? SourceName { get; set; }

    /// <summary>
    /// Create the GitRepository. Off when one for this repository exists already (for example
    /// <c>flux-system</c> from <c>flux bootstrap</c>); set <see cref="SourceName"/> to it.
    /// </summary>
    public bool CreateSource { get; set; } = true;

    /// <summary>Secret with the repository's credentials, for a private repository.</summary>
    public string? SecretRef { get; set; }

    /// <summary>How often Flux checks the repository and reconciles.</summary>
    public string Interval { get; set; } = "1m";

    /// <summary>How long Flux waits for a service's objects to become ready.</summary>
    public string Timeout { get; set; } = "5m";

    /// <summary>Delete objects that are no longer in the repository.</summary>
    public bool Prune { get; set; } = true;
}
