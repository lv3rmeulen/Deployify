using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class DeploymentTargetCapabilitiesTests
{
    [Fact]
    public void GetCapability_UsesTheTypeForAnOrdinaryTarget()
    {
        var plain = Substitute.For<IDeploymentTarget>();
        var rolling = Substitute.For<IDeploymentTarget, IRolloutTarget>();

        plain.GetCapability<IRolloutTarget>().ShouldBeNull();
        plain.Supports<IResourceLifecycle>().ShouldBeFalse();
        rolling.GetCapability<IRolloutTarget>().ShouldBeSameAs(rolling);
    }

    [Fact]
    public void GetCapability_AsksAProviderInsteadOfTheType()
    {
        // The proxy for a plugin implements every interface; only what the plugin declared counts.
        var target = (IDeploymentTarget)Substitute.For([typeof(IDeploymentTarget), typeof(ICapabilityProvider), typeof(IRolloutTarget), typeof(IResourceLifecycle)], []);
        ((ICapabilityProvider)target).GetCapability(typeof(IRolloutTarget)).Returns(target);
        ((ICapabilityProvider)target).GetCapability(typeof(IResourceLifecycle)).Returns((object?)null);

        target.Supports<IRolloutTarget>().ShouldBeTrue();
        target.Supports<IResourceLifecycle>().ShouldBeFalse();
    }
}
