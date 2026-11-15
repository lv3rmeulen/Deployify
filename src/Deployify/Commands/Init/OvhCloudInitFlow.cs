using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Kubernetes;
using Deployify.Target.OvhCloud;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>OVHcloud Managed Kubernetes: the Kubernetes target's manifests, with the kubeconfig from the OVHcloud API.</summary>
internal sealed class OvhCloudInitFlow(KubernetesInitOptions kubernetes) : ITargetInitFlow
{
    private readonly Option<string> _endpoint = new("--ovh-endpoint")
    {
        Description = "OVHcloud API endpoint: ovh-eu | ovh-ca | ovh-us.",
        DefaultValueFactory = _ => "ovh-eu"
    };

    private readonly Option<string?> _project = new("--ovh-project")
    {
        Description = "OVHcloud Public Cloud project ID. Default: OVH_CLOUD_PROJECT_SERVICE at run time."
    };

    private readonly Option<string?> _cluster = new("--ovh-cluster")
    {
        Description = "OVHcloud Managed Kubernetes cluster ID."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.OvhCloud];

    public IEnumerable<Option> Options => [_endpoint, _project, _cluster, kubernetes.NamespacePrefix, kubernetes.IngressDomain];

    // The kubeconfig comes from the API, so no kubectl context.
    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(kubernetes.FromOptions(parseResult, withContext: false),
            parseResult.GetValue(_endpoint)!, parseResult.GetValue(_project), parseResult.GetValue(_cluster));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]OVHcloud Managed Kubernetes settings:[/]");
        var endpoint = await ConsolePrompts.PromptAsync(
            new SelectionPrompt<string>()
                .Title("API endpoint:")
                .AddChoices("ovh-eu", "ovh-ca", "ovh-us"), ct);
        var project = await ConsolePrompts.AskAsync(
            "Public Cloud project ID [dim](blank = OVH_CLOUD_PROJECT_SERVICE)[/]:", string.Empty, ct);
        var cluster = await ConsolePrompts.AskAsync<string>("Managed Kubernetes cluster ID:", ct);
        var (prefix, domain) = await KubernetesInitOptions.PromptNamespaceAndDomainAsync(context, ct);
        AnsiConsole.MarkupLine("[dim]API credentials are read from OVH_APPLICATION_KEY, OVH_APPLICATION_SECRET and " +
                               "OVH_CONSUMER_KEY, or OVH_CLIENT_ID and OVH_CLIENT_SECRET.[/]");
        return Config(KubernetesInitOptions.Build(null, prefix, domain), endpoint, project, cluster);
    }

    private static Dictionary<string, object?> Config(
        KubernetesTargetConfig kubernetesConfig, string endpoint, string? project, string? cluster)
        => new()
        {
            ["kubernetes"] = kubernetesConfig,
            ["ovhcloud"] = new OvhCloudTargetConfig
            {
                Endpoint = OrDefault(endpoint, "ovh-eu"),
                ProjectId = OrNull(project),
                ClusterId = OrNull(cluster)
            }
        };
}
