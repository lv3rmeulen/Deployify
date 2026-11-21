using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

/// <summary><c>deployify logs</c> on Container Apps: the console log stream of each replica of the latest ready revision.</summary>
public sealed class AcaLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AcaDeploymentTarget Target(FakeProcessRunner az)
        => new(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance) { Runner = az };

    private static FakeProcessRunner Az() => new FakeProcessRunner()
        .When(["containerapp", "show"], new ProcessResult(0,
            """{ "properties": { "latestRevisionName": "api--0000004", "latestReadyRevisionName": "api--0000003" } }""", ""))
        .When(["containerapp", "replica", "list"], new ProcessResult(0, """
            [
              { "name": "api--0000003-abc-r1", "properties": { "containers": [ { "name": "daprd" }, { "name": "api" } ] } },
              { "name": "api--0000003-abc-r2", "properties": { "containers": [ { "name": "api" } ] } }
            ]
            """, ""));

    [Fact]
    public async Task ReadsEveryReplicaOfTheLatestReadyRevision()
    {
        var az = Az()
            .When(["--replica", "api--0000003-abc-r1"], new ProcessResult(0,
                """
                {"TimeStamp":"2026-11-21T10:00:00.0000000+00:00","Log":"Connecting to the container 'api'..."}
                {"TimeStamp":"2026-11-21T10:00:01.0000000+00:00","Log":"2026-11-21T10:00:01.123456789Z stdout F hello"}
                {"TimeStamp":"2026-11-21T10:00:03.0000000+00:00","Log":"2026-11-21T10:00:03Z stderr F oops"}
                """, ""))
            .When(["--replica", "api--0000003-abc-r2"], new ProcessResult(0,
                """{"TimeStamp":"2026-11-21T10:00:02.0000000+00:00","Log":"plain text"}""", ""));

        var lines = await Target(az).ReadLogsAsync(AcaFixtures.AcaConfig(), new ServiceLogQuery { Service = "api", Tail = 1000 }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text, l.IsError)).ShouldBe(
        [
            ("abc-r1", "hello", false), ("abc-r2", "plain text", false), ("abc-r1", "oops", true)
        ]);
        var show = az.Single("--replica", "api--0000003-abc-r1");
        show.Args.ShouldContain("api--0000003");
        show.Args[show.Args.ToList().IndexOf("--container") + 1].ShouldBe("api");
        show.Args[show.Args.ToList().IndexOf("--tail") + 1].ShouldBe("300");
        show.Sensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task Since_FiltersTheLinesThePlatformReturns_AndFollowIsPassedOn()
    {
        var az = Az().When(["--replica", "api--0000003-abc-r1"], new ProcessResult(0,
            """
            {"TimeStamp":"2026-11-21T09:00:00+00:00","Log":"old"}
            {"TimeStamp":"2026-11-21T11:00:00+00:00","Log":"new"}
            """, ""));

        var lines = await Target(az).ReadLogsAsync(AcaFixtures.AcaConfig(), new ServiceLogQuery
        {
            Service = "api",
            Since = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero),
            Follow = true
        }, Ct).ToListAsync(Ct);

        lines.Select(l => l.Text).ShouldBe(["new"]);
        az.Single("--replica", "api--0000003-abc-r1").Args.ShouldContain("--follow");
    }

    [Fact]
    public async Task AMissingApp_SaysSo()
    {
        var az = new FakeProcessRunner().When(["containerapp", "show"], new ProcessResult(3, "", "ERROR: (ResourceNotFound) The Resource was not found."));

        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Target(az).ReadLogsAsync(AcaFixtures.AcaConfig(), new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no Container App 'nope'");
    }
}
