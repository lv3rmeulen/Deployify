using System.Collections.Generic;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

public interface IDeploymentTargetFactory
{
    IDeploymentTarget Get(DeploymentTargetType targetType);

    /// <summary>
    /// Like <see cref="Get(DeploymentTargetType)"/>, for the project whose deployify-state.json is in
    /// <paramref name="projectDirectory"/>: plugin targets can come from that project's plugin
    /// folder and lock file.
    /// </summary>
    IDeploymentTarget Get(DeploymentTargetType targetType, string? projectDirectory) => Get(targetType);
    IReadOnlyList<IDeploymentTarget> All { get; }
    bool IsSupported(DeploymentTargetType targetType);
}
