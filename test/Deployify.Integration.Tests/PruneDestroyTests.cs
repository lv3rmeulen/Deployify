using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;
using Deployify.Target.Nomad;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// adopt → apply --prune → destroy through the real CLI, against an in-process fake Nomad.
/// </summary>
public sealed class PruneDestroyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RootCommand Root() => CommandRunner.BuildFull();

    private readonly FakeNomadServer _nomad = new();
    private readonly TempDir _dir = new();

    private async Task SetUpAsync(params string[] runningJobs)
    {
        foreach (var job in runningJobs)
        {
            _nomad.Jobs[job] = true;
        }

        await Harness.WriteStateAsync(_dir.Path, new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = { ["prod"] = Harness.NomadConfig(address: _nomad.Address) }
        }, Ct);
        await Harness.WriteManifestAsync(_dir.Path, ct: Ct);
    }

    private Task<int> RunAsync(params string[] args) => Root().InvokeAsync([.. args, "--project-path", _dir.Path], Ct);

    private async Task<IReadOnlyList<string>> InventoryAsync()
    {
        await using var s = new StateSession(_dir.Path, "prod", null);
        return [.. (await s.Inventory.LoadAsync(Ct)).Services.Select(r => r.Name).Order()];
    }

    [Fact]
    public async Task ApplyWithoutPrune_LeavesARemovedService_AndApplyWithPrune_RemovesIt()
    {
        await SetUpAsync("api", "worker", "redis", "old");
        (await RunAsync("state", "adopt")).ShouldBe(0);
        (await InventoryAsync()).ShouldBe(["api", "old", "redis", "worker"]);

        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);
        _nomad.Jobs.Keys.ShouldContain("old");

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(0);

        _nomad.Jobs.Keys.Order().ShouldBe(["api", "redis", "worker"]);
        _nomad.Purged.ShouldBe(["old"]);
        (await InventoryAsync()).ShouldBe(["api", "redis", "worker"]);
        var journals = await RolloutJournal.ListAsync(_dir.Path, "prod", Ct);
        journals.ShouldContain(j => j.Record.Operation == RolloutOperation.Prune && j.Record.State == RolloutState.Succeeded);
    }

    [Fact]
    public async Task Prune_NeverTouchesAJobThatIsNotInState()
    {
        await SetUpAsync("api", "worker", "redis");
        _nomad.Jobs["someone-elses"] = true; // carries the meta key, but Deployify never recorded it here

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(0);

        _nomad.Jobs.Keys.ShouldContain("someone-elses");
        _nomad.Purged.ShouldBeEmpty();
    }

    [Fact]
    public async Task APruneOfMostServices_IsRefused_UnlessForced()
    {
        await SetUpAsync("api", "worker", "redis", "old1", "old2", "old3", "old4");
        (await RunAsync("state", "adopt")).ShouldBe(0);

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(4);
        _nomad.Purged.ShouldBeEmpty();

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune", "--force-prune")).ShouldBe(0);
        _nomad.Purged.Order().ShouldBe(["old1", "old2", "old3", "old4"]);
    }

    [Fact]
    public async Task Destroy_RemovesDependentsFirst_AndEmptiesTheInventory()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy", "--dry-run")).ShouldBe(0);
        _nomad.Purged.ShouldBeEmpty();

        (await RunAsync("destroy", "--auto-approve")).ShouldBe(0);

        _nomad.Jobs.ShouldBeEmpty();
        _nomad.Purged.IndexOf("worker").ShouldBeLessThan(_nomad.Purged.IndexOf("api")); // worker uses api
        (await InventoryAsync()).ShouldBeEmpty();
        (await RunAsync("destroy", "--auto-approve")).ShouldBe(0); // nothing left: still fine
    }

    [Fact]
    public async Task Destroy_OfOneService_LeavesTheRest()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy", "--service", "redis", "--auto-approve")).ShouldBe(0);
        (await RunAsync("destroy", "--service", "nope", "--auto-approve")).ShouldBe(1);

        _nomad.Purged.ShouldBe(["redis"]);
        (await InventoryAsync()).ShouldBe(["api", "worker"]);
    }

    [Fact]
    public async Task Destroy_WithoutATerminal_NeedsAutoApprove()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy")).ShouldBe(1);

        _nomad.Purged.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _nomad.Dispose();
        _dir.Dispose();
    }
}
