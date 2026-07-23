using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Core.Services;

public sealed class DeploymentTargetFactory : IDeploymentTargetFactory
{
    private readonly Dictionary<DeploymentTargetType, IDeploymentTarget> _targets;

    public DeploymentTargetFactory(IEnumerable<IDeploymentTarget> targets)
    {
        _targets = targets.ToDictionary(t => t.TargetType);
    }

    public IReadOnlyList<IDeploymentTarget> All => _targets.Values.ToList();

    public bool IsSupported(DeploymentTargetType targetType) => _targets.ContainsKey(targetType);

    public IDeploymentTarget Get(DeploymentTargetType targetType)
    {
        if (_targets.TryGetValue(targetType, out var target))
        {
	        return target;
        }

        var supported = string.Join(", ", _targets.Keys.Select(k => k.ToString().ToLower()));
        throw new NotSupportedException(
            $"Target '{targetType.ToString().ToLower()}' is not yet implemented. " +
            $"Supported: {supported}.");
    }
}
