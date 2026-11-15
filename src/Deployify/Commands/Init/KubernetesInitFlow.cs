using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.Helm;
using Spectre.Console;

namespace Deployify.Commands.Init;

/// <summary>Kubernetes (kubectl) and Helm, which deploys the same manifests as a chart.</summary>
internal sealed class KubernetesInitFlow(KubernetesInitOptions kubernetes) : ITargetInitFlow
{
    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.Kubernetes, DeploymentTargetType.Helm];

    public IEnumerable<Option> Options => [kubernetes.Context, kubernetes.NamespacePrefix, kubernetes.IngressDomain];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(target, kubernetes.FromOptions(parseResult));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Kubernetes settings:[/]");
        return Config(target, await KubernetesInitOptions.PromptAsync(context, ct));
    }

    private static Dictionary<string, object?> Config(DeploymentTargetType target, object kubernetesConfig)
    {
        var config = new Dictionary<string, object?> { ["kubernetes"] = kubernetesConfig };
        if (target == DeploymentTargetType.Helm)
        {
            // Helm deploys the Kubernetes target's manifests, so it reads the same settings.
            config["helm"] = new HelmTargetConfig();
        }

        return config;
    }
}
