using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Rollouts;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

public sealed class RolloutCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RootCommand Root() => CommandRunner.BuildFull();
    private const string EcsDisplayName = "AWS ECS (Fargate)";

    private static async Task<string> WriteJournalAsync(string dir, ServiceRolloutState state)
    {
        await using var journal = await RolloutJournal.BeginAsync(dir, "local", EcsDisplayName, [["api"]], ct: Ct);
        var api = journal.Record.Service("api");
        api.Snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "arn:td/api:6" };
        api.Revision = "arn:td/api:7";
        api.State = state;
        journal.Record.State = RolloutState.Succeeded;
        await journal.SaveAsync(Ct);
        return journal.Record.Id;
    }

    [Fact]
    public async Task Status_WithoutRollouts_ReturnsZero()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);

        (await Root().InvokeAsync(["status", "--project-path", dir.Path], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Status_ShowsARecordedRollout_AndRejectsAnUnknownId()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        var id = await WriteJournalAsync(dir.Path, ServiceRolloutState.Healthy);

        (await Root().InvokeAsync(["status", "--project-path", dir.Path], Ct)).ShouldBe(0);
        (await Root().InvokeAsync(["status", "--project-path", dir.Path, "--rollout", id], Ct)).ShouldBe(0);
        (await Root().InvokeAsync(["status", "--project-path", dir.Path, "--rollout", "nope"], Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_WithoutRollouts_ReturnsOne()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);

        (await Root().InvokeAsync(["rollback", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_OfARolloutToAnotherTarget_IsRefused()
    {
        using var dir = new TempDir();
        await Harness.WriteComposeStateAsync(dir.Path, ct: Ct);
        await WriteJournalAsync(dir.Path, ServiceRolloutState.Healthy); // recorded against ECS

        (await Root().InvokeAsync(["rollback", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_OfARolloutAlreadyRolledBack_HasNothingToDo()
    {
        using var dir = new TempDir();
        await Harness.WriteEcsStateAsync(dir.Path, ct: Ct);
        await WriteJournalAsync(dir.Path, ServiceRolloutState.RolledBack);

        (await Root().InvokeAsync(["rollback", "--project-path", dir.Path, "--auto-approve"], Ct)).ShouldBe(0);
    }
}
