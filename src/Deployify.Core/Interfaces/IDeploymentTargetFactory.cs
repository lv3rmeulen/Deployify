using System.Collections.Generic;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

public interface IDeploymentTargetFactory
{
    IDeploymentTarget Get(DeploymentTargetType targetType);
    IReadOnlyList<IDeploymentTarget> All { get; }
    bool IsSupported(DeploymentTargetType targetType);
}
