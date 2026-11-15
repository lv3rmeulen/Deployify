using System.Collections.Generic;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Target.GitOps;
using Deployify.Target.Kubernetes;
using Spectre.Console;
using static Deployify.Commands.Init.TargetInitFlows;

namespace Deployify.Commands.Init;

/// <summary>Argo CD and Flux: the Kubernetes target's manifests, committed to a config repository.</summary>
internal sealed class GitOpsInitFlow(KubernetesInitOptions kubernetes) : ITargetInitFlow
{
    private readonly Option<string?> _repo = new("--gitops-repo")
    {
        Description = "Config repository the GitOps controller watches (https or ssh URL)."
    };

    private readonly Option<string> _branch = new("--gitops-branch")
    {
        Description = "Branch Deployify commits to.",
        DefaultValueFactory = _ => "main"
    };

    private readonly Option<string?> _path = new("--gitops-path")
    {
        Description = "Folder in the repository for this environment. Default: deployify/{env}."
    };

    public IReadOnlyList<DeploymentTargetType> Targets { get; } = [DeploymentTargetType.ArgoCd, DeploymentTargetType.Flux];

    public IEnumerable<Option> Options =>
        [kubernetes.Context, kubernetes.NamespacePrefix, kubernetes.IngressDomain, _repo, _branch, _path];

    public Dictionary<string, object?> FromOptions(DeploymentTargetType target, ParseResult parseResult, InitContext context)
        => Config(target, kubernetes.FromOptions(parseResult),
            parseResult.GetValue(_repo), parseResult.GetValue(_branch)!, parseResult.GetValue(_path));

    public async Task<Dictionary<string, object?>> PromptAsync(DeploymentTargetType target, InitContext context, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[dim]Kubernetes settings:[/]");
        var kubernetesConfig = await KubernetesInitOptions.PromptAsync(context, ct);

        AnsiConsole.MarkupLine("[dim]GitOps settings:[/]");
        var repo = await ConsolePrompts.AskAsync("Config repository URL:", string.Empty, ct);
        var branch = await ConsolePrompts.AskAsync("Branch:", "main", ct);
        var path = await ConsolePrompts.AskAsync("Folder in the repository:", $"deployify/{context.EnvironmentName}", ct);
        return Config(target, kubernetesConfig, repo, branch, path);
    }

    // The GitOps targets render the Kubernetes target's manifests and commit them.
    private static Dictionary<string, object?> Config(
        DeploymentTargetType target, KubernetesTargetConfig kubernetesConfig, string? repo, string branch, string? path)
        => new()
        {
            ["kubernetes"] = kubernetesConfig,
            ["gitops"] = new GitOpsTargetConfig
            {
                RepoUrl = repo ?? string.Empty,
                Branch = OrDefault(branch, "main"),
                Path = OrNull(path)
            },
            [target == DeploymentTargetType.ArgoCd ? "argocd" : "flux"] = target == DeploymentTargetType.ArgoCd
                ? new ArgoCdTargetConfig()
                : new FluxTargetConfig()
        };
}
