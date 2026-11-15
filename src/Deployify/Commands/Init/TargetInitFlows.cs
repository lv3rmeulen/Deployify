using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Deployify.Core.Models;

namespace Deployify.Commands.Init;

/// <summary>The built-in targets' init flows. Each instance owns its own option objects.</summary>
internal sealed class TargetInitFlows
{
    public TargetInitFlows()
    {
        // Kubernetes, Helm, the GitOps targets and OVHcloud deploy the same manifests, so they
        // share the namespace prefix and ingress domain flags.
        var kubernetes = new KubernetesInitOptions();

        // Flow order sets the order of the flags in --help.
        All =
        [
            new NomadInitFlow(),
            new KubernetesInitFlow(kubernetes),
            new GitOpsInitFlow(kubernetes),
            new OvhCloudInitFlow(kubernetes),
            new IisInitFlow(),
            new ComposeInitFlow(),
            new AwsEcsInitFlow(),
            new AzureContainerAppsInitFlow(),
            new NomadConsulInitFlow()
        ];
    }

    public IReadOnlyList<ITargetInitFlow> All { get; }

    /// <summary>The targets the interactive prompt offers, in the order it lists them.</summary>
    public static IReadOnlyList<DeploymentTargetType> Choices { get; } =
    [
        DeploymentTargetType.Nomad,
        DeploymentTargetType.Kubernetes,
        DeploymentTargetType.Compose,
        DeploymentTargetType.AwsEcs,
        DeploymentTargetType.AzureContainerApps,
        DeploymentTargetType.NomadConsul,
        DeploymentTargetType.Helm,
        DeploymentTargetType.ArgoCd,
        DeploymentTargetType.Flux,
        DeploymentTargetType.OvhCloud,
        DeploymentTargetType.Iis
    ];

    /// <summary>Every flow's flags, each listed once.</summary>
    public IEnumerable<Option> Options => All.SelectMany(f => f.Options).Distinct();

    /// <summary>Every flow's secret flags, each listed once.</summary>
    public IEnumerable<SecretFlag> SecretFlags => All.SelectMany(f => f.SecretFlags).Distinct();

    /// <summary>The flow for <paramref name="target"/>, or null for a target without one (a plugin target).</summary>
    public ITargetInitFlow? Find(DeploymentTargetType target) => All.FirstOrDefault(f => f.Targets.Contains(target));

    /// <summary>Null when <paramref name="value"/> is blank, so optional settings stay out of the state file.</summary>
    internal static string? OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary><paramref name="fallback"/> when <paramref name="value"/> is blank.</summary>
    internal static string OrDefault(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>A comma-separated list, trimmed, without empty entries.</summary>
    internal static List<string> SplitList(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
