using Deployify.Core.Interfaces;
using Deployify.Core.Rollouts;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Rollouts;

public sealed class RolloutPlannerTests
{
    private static ServiceDependency Uses(string user, string used) => new() { From = used, To = user };

    [Fact]
    public void Chain_GivesOneWavePerStep()
    {
        var waves = RolloutPlanner.Waves(["web", "api", "db"], [Uses("web", "api"), Uses("api", "db")]);

        waves.Count.ShouldBe(3);
        waves[0].ShouldBe(["db"]);
        waves[1].ShouldBe(["api"]);
        waves[2].ShouldBe(["web"]);
    }

    [Fact]
    public void IndependentServices_ShareAWave_InInputOrder()
    {
        var waves = RolloutPlanner.Waves(["worker", "api", "db"], [Uses("worker", "db"), Uses("api", "db")]);

        waves.Count.ShouldBe(2);
        waves[1].ShouldBe(["worker", "api"]);
    }

    [Fact]
    public void EdgesToUnknownServices_AreIgnored()
    {
        var waves = RolloutPlanner.Waves(["api"], [Uses("api", "some-parameter")]);

        waves.Count.ShouldBe(1);
        waves[0].ShouldBe(["api"]);
    }

    [Fact]
    public void Cycle_EndsUpInOneFinalWave()
    {
        var waves = RolloutPlanner.Waves(["db", "a", "b"], [Uses("a", "b"), Uses("b", "a"), Uses("a", "db")]);

        waves.Count.ShouldBe(2);
        waves[0].ShouldBe(["db"]);
        waves[1].ShouldBe(["a", "b"]);
    }
}
