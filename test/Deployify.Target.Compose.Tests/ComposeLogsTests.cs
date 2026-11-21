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

namespace Deployify.Target.Compose.Tests;

/// <summary><c>deployify logs</c> on Docker Compose: docker logs of the service's containers, found by label.</summary>
public sealed class ComposeLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ComposeDeploymentTarget Target(FakeProcessRunner docker)
        => new(Substitute.For<IPublisher>(), NullLogger<ComposeDeploymentTarget>.Instance) { Processes = docker };

    private static FakeProcessRunner Docker(string ps) => new FakeProcessRunner()
        .When(["ps", "-a"], new ProcessResult(0, ps, ""));

    [Fact]
    public async Task ReadsEveryRunningContainer_StdoutAndStderrInTimeOrder()
    {
        var docker = Docker("c2\ttestproject-api-2\trunning\nc1\ttestproject-api-1\trunning\nc0\ttestproject-api-old\texited\n")
            .When(["logs", "--timestamps", "--tail", "100", "c1"], new ProcessResult(0,
                "2026-11-21T10:00:01.000000000Z one\n2026-11-21T10:00:04Z four\n", "2026-11-21T10:00:02Z two (stderr)\n"))
            .When(["logs", "--timestamps", "--tail", "100", "c2"], new ProcessResult(0, "2026-11-21T10:00:03Z three\n", ""));

        var lines = await Target(docker).ReadLogsAsync(Fixtures.ComposeConfig(), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text, l.IsError)).ShouldBe(
        [
            ("api-1", "one", false), ("api-1", "two (stderr)", true), ("api-2", "three", false), ("api-1", "four", false)
        ]);
        docker.Single("ps", "-a").Args.ShouldContain("label=com.docker.compose.project=testproject");
        docker.Single("ps", "-a").Args.ShouldContain("label=com.docker.compose.service=api");
        docker.Calls.ShouldNotContain(c => c.Args.Contains("c0"));
        docker.Calls.Where(c => c.Args.Contains("logs")).ShouldAllBe(c => c.Sensitive);
    }

    [Fact]
    public async Task WhenNothingRuns_TheNewestContainerIsRead_WithTheOptionsPassedOn()
    {
        var docker = Docker("new\ttestproject-api-1\texited\nold\ttestproject-api-1-old\texited\n");

        await Target(docker).ReadLogsAsync(Fixtures.ComposeConfig(), new ServiceLogQuery
        {
            Service = "api",
            Tail = null,
            Since = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero),
            Follow = true
        }, Ct).ToListAsync(Ct);

        docker.Single("logs").Args.ShouldBe(["logs", "--timestamps", "--tail", "all", "--since", "2026-11-21T09:00:00.0000000Z", "--follow", "new"]);
    }

    [Fact]
    public async Task AServiceWithoutContainers_SaysSo_AndDockerErrorsAreReported()
    {
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Target(Docker("")).ReadLogsAsync(Fixtures.ComposeConfig(), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no containers for 'api'");

        var failing = Docker("c1\ttestproject-api-1\trunning\n")
            .When(["logs"], new ProcessResult(1, "", "Error response from daemon: No such container: c1\n"));
        ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Target(failing).ReadLogsAsync(Fixtures.ComposeConfig(), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("No such container");
    }
}
