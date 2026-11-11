using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Services;

public sealed class DeploymentTargetFactory : IDeploymentTargetFactory
{
    private readonly Dictionary<DeploymentTargetType, IDeploymentTarget> _targets;
    private readonly IReadOnlyList<IDeploymentTargetSource> _sources;

    public DeploymentTargetFactory(IEnumerable<IDeploymentTarget> targets)
        : this(targets, [])
    {
    }

    /// <param name="sources">
    /// Asked, in order, for a target that isn't among <paramref name="targets"/> (plugin targets).
    /// A registered target always wins, so a plugin can't replace a built-in one.
    /// </param>
    public DeploymentTargetFactory(IEnumerable<IDeploymentTarget> targets, IEnumerable<IDeploymentTargetSource> sources)
    {
        _targets = targets.ToDictionary(t => t.TargetType);
        _sources = [.. sources];
    }

    /// <summary>The registered targets. Plugin targets are found on demand and aren't listed here.</summary>
    public IReadOnlyList<IDeploymentTarget> All => [.. _targets.Values];

    public bool IsSupported(DeploymentTargetType targetType)
        => _targets.ContainsKey(targetType) || _sources.Any(s => s.Find(targetType, null) is not null);

    public IDeploymentTarget Get(DeploymentTargetType targetType) => Get(targetType, null);

    public IDeploymentTarget Get(DeploymentTargetType targetType, string? projectDirectory)
    {
        if (_targets.TryGetValue(targetType, out var target))
        {
            return target;
        }

        foreach (var source in _sources)
        {
            if (source.Find(targetType, projectDirectory) is { } found)
            {
                return found;
            }
        }

        var supported = string.Join(", ", _targets.Keys.Select(k => k.ToString().ToLowerInvariant()));
        var hint = targetType.IsBuiltIn
            ? string.Empty
            : $" If '{targetType}' comes from a plugin, check that the plugin is installed: run 'deployify plugin list'.";
        throw new NotSupportedException(
            $"Target '{targetType.ToString().ToLowerInvariant()}' is not available. Supported: {supported}.{hint}");
    }
}
