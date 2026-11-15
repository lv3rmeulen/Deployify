using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Target.Kubernetes;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>
/// The <c>kubernetes</c> settings shared by every target that deploys the Kubernetes target's
/// manifests: Kubernetes, Helm, Argo CD, Flux and OVHcloud.
/// </summary>
internal sealed class KubernetesInitOptions
{
    public Option<string?> Context { get; } = new("--kubernetes-context")
    {
        Description = "kubectl context name."
    };

    public Option<string> NamespacePrefix { get; } = new("--kubernetes-namespace-prefix")
    {
        Description = "Kubernetes namespace prefix.",
        DefaultValueFactory = _ => "deployify"
    };

    public Option<string?> IngressDomain { get; } = new("--kubernetes-ingress-domain")
    {
        Description = "Ingress base domain."
    };

    public KubernetesTargetConfig FromOptions(ParseResult parseResult, bool withContext = true)
        => Build(
            withContext ? parseResult.GetValue(Context) : null,
            parseResult.GetValue(NamespacePrefix)!,
            parseResult.GetValue(IngressDomain));

    public static Task<string> PromptContextAsync(CancellationToken ct)
        => ConsolePrompts.AskAsync("kubectl context [dim](blank = current)[/]:", string.Empty, ct);

    public static async Task<(string Prefix, string Domain)> PromptNamespaceAndDomainAsync(InitContext context, CancellationToken ct)
    {
        var prefix = await ConsolePrompts.AskAsync("Namespace prefix:", context.EnvironmentName, ct);
        var domain = await ConsolePrompts.AskAsync("Ingress base domain [dim](blank to skip)[/]:", string.Empty, ct);
        return (prefix, domain);
    }

    /// <summary>Context, namespace prefix and ingress domain, as Kubernetes, Helm, Argo CD and Flux ask for them.</summary>
    public static async Task<KubernetesTargetConfig> PromptAsync(InitContext context, CancellationToken ct)
    {
        var kubeContext = await PromptContextAsync(ct);
        var (prefix, domain) = await PromptNamespaceAndDomainAsync(context, ct);
        return Build(kubeContext, prefix, domain);
    }

    public static KubernetesTargetConfig Build(string? context, string prefix, string? ingressDomain) => new()
    {
        Context = OrNull(context),
        NamespacePrefix = prefix,
        IngressBaseDomain = OrNull(ingressDomain),
        GenerateIngress = !string.IsNullOrWhiteSpace(ingressDomain)
    };
}
