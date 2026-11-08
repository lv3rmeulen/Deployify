using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Compose.Tests;

/// <summary>Plan and diff share one comparison; these pin how docker compose's answers are reported.</summary>
public sealed class ComposePlanTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private static ComposeDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<ComposeDeploymentTarget>.Instance) { Processes = fake };

    /// <summary>docker compose with "api" running and the given dry-run answer.</summary>
    private static FakeProcessRunner Docker(ProcessResult dryRun)
        => new FakeProcessRunner()
            .When(["ps", "--services"], new ProcessResult(0, "api\n", ""))
            .When(["--dry-run"], dryRun);

    [Fact]
    public async Task NoDryRunSupport_IsUnknown_NotUpdate()
    {
        var fake = Docker(new ProcessResult(1, "", "unknown flag: --dry-run"));

        var plan = await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), _dir.Path, Ct);

        plan.ToUpdate.ShouldBeEmpty();
        plan.Unknown.ShouldHaveSingleItem().Reason.ShouldContain("dry-run");
    }

    [Theory]
    [InlineData("DRY-RUN MODE -  Container testproject-api-1  Recreate", DiffKind.Changed)]
    [InlineData("DRY-RUN MODE -  Container testproject-api-1  Running", DiffKind.Unchanged)]
    public async Task PlanAndDiff_ReadTheDryRunTheSameWay(string dryRunOutput, DiffKind expected)
    {
        var target = Target(Docker(new ProcessResult(0, dryRunOutput, "")));
        var config = Fixtures.ComposeConfig();

        var diff = await target.DiffAsync(Fixtures.SingleService("api"), config, _dir.Path, Ct);
        var plan = await target.PlanAsync(Fixtures.SingleService("api"), config, _dir.Path, Ct);

        diff.ShouldHaveSingleItem().Kind.ShouldBe(expected);
        (expected == DiffKind.Changed ? plan.ToUpdate : plan.Unchanged).ShouldBe(["api"]);
    }

    [Fact]
    public async Task DryRun_UsesTheSameProfilesAsApply()
    {
        var fake = Docker(new ProcessResult(0, "", ""));

        await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(profiles: true), _dir.Path, Ct);

        fake.Single("--dry-run").Args.ShouldContain("--profile");
        fake.Single("--dry-run").Args.ShouldContain("prod");
    }

    [Fact]
    public async Task NoComposeCli_AssumesAllNew()
    {
        var fake = new FakeProcessRunner { Missing = { "docker", "docker-compose" } };

        var plan = await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), _dir.Path, Ct);

        plan.ToCreate.ShouldBe(["api"]);
    }

    [Fact]
    public async Task ProjectNameWithSpace_IsOneArgument()
    {
        var fake = Docker(new ProcessResult(0, "", ""));

        await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(project: "my shop"), _dir.Path, Ct);

        var ps = fake.Single("ps", "--services");
        ps.FileName.ShouldBe("docker");
        ps.Args.Take(3).ShouldBe(["compose", "-p", "my shop"]);
    }

    [Fact]
    public void WouldRecreate_MatchesOnlyThisProjectsContainer()
    {
        ComposeDeploymentTarget.WouldRecreate("Container shop-api-1  Recreate", "shop", "api").ShouldBeTrue();
        ComposeDeploymentTarget.WouldRecreate("Container shop-api-worker-1  Recreate", "shop", "api").ShouldBeFalse();
        ComposeDeploymentTarget.WouldRecreate("Container other-api-1  Create", "shop", "api").ShouldBeFalse();
    }
}
