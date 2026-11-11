using System;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class DeploymentTargetFactoryTests
{
    private static IDeploymentTarget Mock(DeploymentTargetType type)
    {
        var t = Substitute.For<IDeploymentTarget>();
        t.TargetType.Returns(type);
        return t;
    }

    [Fact]
    public void Get_ReturnsCorrectTarget_WhenRegistered()
    {
        var nomad = Mock(DeploymentTargetType.Nomad);
        var factory = new DeploymentTargetFactory([nomad]);
        factory.Get(DeploymentTargetType.Nomad).ShouldBeSameAs(nomad);
    }

    [Fact]
    public void Get_ThrowsNotSupportedException_WhenTargetMissing()
    {
        var act = () => new DeploymentTargetFactory([]).Get(DeploymentTargetType.Kubernetes);
        Should.Throw<NotSupportedException>(act).Message.ToLower().ShouldContain("kubernetes");
    }

    [Fact]
    public void Get_ErrorMessage_ListsSupportedTargets()
    {
        var act = () => new DeploymentTargetFactory([Mock(DeploymentTargetType.Nomad)]).Get(DeploymentTargetType.Compose);
        Should.Throw<NotSupportedException>(act).Message.ToLower().ShouldContain("nomad");
    }

    [Theory]
    [InlineData(DeploymentTargetType.Names.Nomad, true)]
    [InlineData(DeploymentTargetType.Names.Kubernetes, false)]
    public void IsSupported_ReturnsExpected(string type, bool expected)
    {
        var factory = new DeploymentTargetFactory([Mock(DeploymentTargetType.Nomad)]);
        factory.IsSupported(DeploymentTargetType.Parse(type)).ShouldBe(expected);
    }

    [Fact]
    public void Get_FindsPluginTarget_CaseInsensitively()
    {
        var example = Mock(DeploymentTargetType.Parse("example"));
        var factory = new DeploymentTargetFactory([example]);
        factory.Get(DeploymentTargetType.Parse("EXAMPLE")).ShouldBeSameAs(example);
    }

    [Fact]
    public void All_ReturnsAllRegisteredTargets()
    {
        var factory = new DeploymentTargetFactory([
            Mock(DeploymentTargetType.Nomad),
            Mock(DeploymentTargetType.Kubernetes)
        ]);
        factory.All.Count.ShouldBe(2);
    }

    [Fact]
    public void Constructor_ThrowsOnDuplicateTargetType()
    {
        var act = () => new DeploymentTargetFactory([
            Mock(DeploymentTargetType.Nomad),
            Mock(DeploymentTargetType.Nomad)
        ]);
        Should.Throw<Exception>(act);
    }

    [Theory]
    [InlineData(DeploymentTargetType.Names.Nomad)]
    [InlineData(DeploymentTargetType.Names.Kubernetes)]
    [InlineData(DeploymentTargetType.Names.Compose)]
    public void Get_ReturnsTarget_ForEachKnownType(string name)
    {
        var factory = new DeploymentTargetFactory([
            Mock(DeploymentTargetType.Nomad),
            Mock(DeploymentTargetType.Kubernetes),
            Mock(DeploymentTargetType.Compose)
        ]);
        factory.Get(DeploymentTargetType.Parse(name)).ShouldNotBeNull();
    }
}
