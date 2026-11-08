using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Lifecycle;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;
using Deployify.Core.Tests.Rollouts;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Lifecycle;

public sealed class RemovalPlannerTests
{
    private static EnvironmentInventory Inventory(params (string Name, string[] DependsOn)[] services)
    {
        var inv = new EnvironmentInventory { Environment = "prod" };
        foreach (var (name, deps) in services)
        {
            inv.Upsert(name, deps, DateTimeOffset.UtcNow);
        }

        return inv;
    }

    private static ManagedService M(string name, string? env = null) => new(name, Environment: env);

    [Fact]
    public void AServiceIsDeleted_OnlyWhenItIsBothInStateAndMarkedOnThePlatform()
    {
        var plan = RemovalPlanner.Build(
            ["api", "gone", "stranger"],
            Inventory(("api", []), ("gone", [])),
            [M("api"), M("stranger")],
            "prod");

        plan.ToDelete.ShouldBe(["api"]);
        plan.AlreadyGone.ShouldBe(["gone"]);
        plan.NotInState.ShouldBe(["stranger"]);
    }

    [Fact]
    public void AServiceTaggedForAnotherEnvironment_IsLeftAlone_EvenWhenInState()
    {
        var plan = RemovalPlanner.Build(["api", "web"], Inventory(("api", []), ("web", [])),
            [M("api", "prod"), M("web", "staging")], "prod");

        plan.ToDelete.ShouldBe(["api"]);
        plan.OtherEnvironment.ShouldBe(["web"]);
        plan.AlreadyGone.ShouldBeEmpty();
        plan.ManagedCount.ShouldBe(1);
    }

    [Fact]
    public void Dependents_AreDeletedBeforeTheServicesTheyUse()
    {
        // web → api → db, cache standalone
        var plan = RemovalPlanner.Build(["db", "api", "web", "cache"],
            Inventory(("db", []), ("api", ["db"]), ("web", ["api"]), ("cache", [])),
            [M("db"), M("api"), M("web"), M("cache")], "prod");

        plan.ToDelete.IndexOf("web").ShouldBeLessThan(plan.ToDelete.IndexOf("api"));
        plan.ToDelete.IndexOf("api").ShouldBeLessThan(plan.ToDelete.IndexOf("db"));
        plan.ToDelete.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData(1, 1, true)]   // the only service
    [InlineData(1, 2, false)]  // one of two
    [InlineData(2, 3, true)]   // more than half
    [InlineData(2, 4, false)]  // exactly half
    [InlineData(3, 3, true)]   // everything
    [InlineData(0, 3, false)]
    public void ThePruneSafetyCheck_StopsMassDeletions(int deleting, int managed, bool refused)
    {
        var names = Enumerable.Range(0, managed).Select(i => $"s{i}").ToList();
        var inv = Inventory([.. names.Select(n => (n, Array.Empty<string>()))]);
        var plan = RemovalPlanner.Build(names.Take(deleting), inv, [.. names.Select(n => M(n))], "prod");

        (RemovalPlanner.PruneSafetyCheck(plan) is not null).ShouldBe(refused);
    }
}

public sealed class ServiceRemoverTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _dir = new();
    private readonly StateSession _state;
    private readonly IResourceLifecycle _lifecycle = Substitute.For<IResourceLifecycle>();

    public ServiceRemoverTests() => _state = new StateSession(_dir.Path, "prod", null);

    private async Task<RemovalPlan> PlanAsync(params string[] services)
    {
        var inv = await _state.Inventory.UpdateAsync(i =>
        {
            foreach (var s in services)
            {
                i.Upsert(s, s == "api" ? ["db"] : [], DateTimeOffset.UtcNow);
            }
        }, Ct);
        return RemovalPlanner.Build(services, inv, [.. services.Select(s => new ManagedService(s))], "prod");
    }

    private Task<RemovalResult> RunAsync(RemovalPlan plan, bool whole = true)
        => ServiceRemover.RunAsync(new RemovalRequest
        {
            Target = FakeRolloutTarget.Target(["api", "db"]),
            Lifecycle = _lifecycle,
            Config = new EnvironmentConfig(),
            EnvironmentName = "prod",
            State = _state,
            Plan = plan,
            Operation = RolloutOperation.Destroy,
            Options = new DeleteOptions { KeepSecrets = true },
            WholeEnvironment = whole
        }, Ct);

    [Fact]
    public async Task Deletes_InOrder_EmptiesTheInventory_AndCleansUpTheEnvironment()
    {
        var order = new List<string>();
        _lifecycle.DeleteServiceAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(ci => { order.Add(ci.ArgAt<string>(1)); return Task.CompletedTask; });
        _lifecycle.DestroyEnvironmentAsync(default!, default!, Ct).ReturnsForAnyArgs(["network shop"]);

        var result = await RunAsync(await PlanAsync("api", "db"));

        result.Succeeded.ShouldBeTrue();
        order.ShouldBe(["api", "db"]);
        result.EnvironmentResourcesRemoved.ShouldBe(["network shop"]);
        (await _state.Inventory.LoadAsync(Ct)).Services.ShouldBeEmpty();
        await _lifecycle.Received(2).DeleteServiceAsync(Arg.Any<EnvironmentConfig>(), Arg.Any<string>(),
            Arg.Is<DeleteOptions>(o => o.KeepSecrets), Arg.Any<CancellationToken>());

        var journal = (await RolloutJournal.ListAsync(_dir.Path, "prod", Ct)).Single().Record;
        journal.Operation.ShouldBe(RolloutOperation.Destroy);
        journal.State.ShouldBe(RolloutState.Succeeded);
        journal.Services.ShouldAllBe(s => s.State == ServiceRolloutState.Deleted);
    }

    [Fact]
    public async Task AFailure_Stops_AndLeavesWhatIsLeftInState_SoARerunContinues()
    {
        _lifecycle.DeleteServiceAsync(Arg.Any<EnvironmentConfig>(), "api", Arg.Any<DeleteOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("access denied"));

        var result = await RunAsync(await PlanAsync("api", "db"));

        result.Succeeded.ShouldBeFalse();
        result.FailedService.ShouldBe("api");
        result.Error.ShouldBe("access denied");
        await _lifecycle.DidNotReceive().DeleteServiceAsync(Arg.Any<EnvironmentConfig>(), "db", Arg.Any<DeleteOptions>(), Arg.Any<CancellationToken>());
        await _lifecycle.DidNotReceiveWithAnyArgs().DestroyEnvironmentAsync(default!, default!, Ct);
        (await _state.Inventory.LoadAsync(Ct)).Services.Select(s => s.Name).Order().ShouldBe(["api", "db"]);
    }

    [Fact]
    public async Task ServicesAlreadyGone_AreOnlyDroppedFromState()
    {
        var inv = await _state.Inventory.UpdateAsync(i => i.Upsert("old", [], DateTimeOffset.UtcNow), Ct);
        var plan = RemovalPlanner.Build(["old"], inv, [], "prod");

        var result = await RunAsync(plan, whole: false);

        result.Succeeded.ShouldBeTrue();
        await _lifecycle.DidNotReceiveWithAnyArgs().DeleteServiceAsync(default!, default!, default!, Ct);
        (await _state.Inventory.LoadAsync(Ct)).Services.ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        await _state.DisposeAsync();
        _dir.Dispose();
    }
}

public sealed class InventoryRecorderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _dir = new();

    [Fact]
    public async Task RecordDesired_ListsEveryServiceWithItsDependencies()
    {
        await using var state = new StateSession(_dir.Path, "prod", null);
        var target = FakeRolloutTarget.Target(["api", "db"], new Dictionary<string, string[]> { ["api"] = ["db"] });

        var inv = await InventoryRecorder.RecordDesiredAsync(state, target, new AspireManifest(), new EnvironmentConfig(), ct: Ct);

        inv.Target.ShouldBe("Fake");
        inv.Find("api")!.DependsOn.ShouldBe(["db"]);
        inv.Find("db")!.DependsOn.ShouldBeEmpty();
    }

    [Fact]
    public async Task Adopt_AddsOnlyUnrecordedServicesOfThisEnvironment()
    {
        await using var state = new StateSession(_dir.Path, "prod", null);
        await state.Inventory.UpdateAsync(i => i.Upsert("api", [], DateTimeOffset.UtcNow), Ct);

        var added = await InventoryRecorder.AdoptAsync(state, FakeRolloutTarget.Target([]),
            [new ManagedService("api"), new ManagedService("legacy", "arn:legacy"), new ManagedService("other", Environment: "staging")],
            "prod", ct: Ct);

        added.ShouldBe(["legacy"]);
        var inv = await state.Inventory.LoadAsync(Ct);
        inv.Services.Select(s => s.Name).Order().ShouldBe(["api", "legacy"]);
        inv.Find("legacy")!.TargetId.ShouldBe("arn:legacy");
    }

    public void Dispose() => _dir.Dispose();
}
