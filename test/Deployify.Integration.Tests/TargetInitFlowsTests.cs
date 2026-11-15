using System.Linq;
using Deployify.Commands.Init;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class TargetInitFlowsTests
{
    [Fact]
    public void EveryChoice_HasExactlyOneFlow()
    {
        var flows = new TargetInitFlows();

        foreach (var target in TargetInitFlows.Choices)
        {
            flows.All.Count(f => f.Targets.Contains(target)).ShouldBe(1, target.ToString());
        }
    }

    [Fact]
    public void EveryFlowTarget_IsOffered()
        => new TargetInitFlows().All.SelectMany(f => f.Targets).ShouldBeSubsetOf(TargetInitFlows.Choices);

    [Fact]
    public void Flags_AreUnique()
    {
        var names = new TargetInitFlows().Options.Select(o => o.Name).ToList();
        names.ShouldBeUnique();
    }

    [Fact]
    public void SecretFlags_AreFlowOptions()
    {
        var flows = new TargetInitFlows();
        flows.SecretFlags.Select(f => f.Flag).ShouldBeSubsetOf(flows.Options);
    }
}
