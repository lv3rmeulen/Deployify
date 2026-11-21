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

namespace Deployify.Target.AwsEcs.Tests;

/// <summary><c>deployify logs</c> on ECS: the tasks' CloudWatch Logs streams.</summary>
public sealed class EcsLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EcsDeploymentTarget Target(FakeProcessRunner aws)
        => new(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance) { Runner = aws };

    private static long Ms(int second) => new DateTimeOffset(2026, 11, 21, 10, 0, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public async Task ReadsTheLastLinesOfEachRunningTask_InTimeOrder()
    {
        var aws = new FakeProcessRunner()
            .When(["ecs", "list-tasks"], new ProcessResult(0,
                """{ "taskArns": ["arn:aws:ecs:us-east-1:1:task/test-cluster/0123456789abcdef", "arn:aws:ecs:us-east-1:1:task/test-cluster/fedcba9876543210"] }""", ""))
            .When(["--log-stream-name", "api/api/0123456789abcdef"], new ProcessResult(0,
                $$"""{ "events": [ { "timestamp": {{Ms(1)}}, "message": "one\n" }, { "timestamp": {{Ms(3)}}, "message": "three" } ] }""", ""))
            .When(["--log-stream-name", "api/api/fedcba9876543210"], new ProcessResult(0,
                $$"""{ "events": [ { "timestamp": {{Ms(2)}}, "message": "two" } ] }""", ""));

        var lines = await Target(aws).ReadLogsAsync(EcsFixtures.EcsConfig(), new ServiceLogQuery { Service = "api", Tail = 20 }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text)).ShouldBe([("01234567", "one"), ("fedcba98", "two"), ("01234567", "three")]);
        var get = aws.Single("--log-stream-name", "api/api/0123456789abcdef");
        get.Args.ShouldContain("/deployify/test-cluster");
        get.Args.ShouldContain("--no-start-from-head");
        get.Args[get.Args.ToList().IndexOf("--limit") + 1].ShouldBe("20");
        get.Sensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task WithNoRunningTask_TheLatestStreamIsRead()
    {
        var aws = new FakeProcessRunner()
            .When(["ecs", "list-tasks"], new ProcessResult(0, """{ "taskArns": [] }""", ""))
            .When(["describe-log-streams"], new ProcessResult(0,
                """{ "logStreams": [ { "logStreamName": "api/api/old", "lastEventTimestamp": 1 }, { "logStreamName": "api/api/crashed1234", "lastEventTimestamp": 9 } ] }""", ""))
            .When(["--log-stream-name", "api/api/crashed1234"], new ProcessResult(0,
                $$"""{ "events": [ { "timestamp": {{Ms(1)}}, "message": "boom" } ] }""", ""));

        var lines = await Target(aws).ReadLogsAsync(EcsFixtures.EcsConfig(), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct);

        lines.Single().Text.ShouldBe("boom");
        lines.Single().Instance.ShouldBe("crashed1");
    }

    [Fact]
    public async Task Following_TailsTheServicePrefix_FromTheNewestLineShown_WithoutRepeatingIt()
    {
        var aws = new FakeProcessRunner()
            .When(["ecs", "list-tasks"], new ProcessResult(0, """{ "taskArns": ["arn:task/test-cluster/0123456789abcdef"] }""", ""))
            .When(["--log-stream-name", "api/api/0123456789abcdef"], new ProcessResult(0,
                $$"""{ "events": [ { "timestamp": {{Ms(1)}}, "message": "shown" } ] }""", ""))
            .When(["logs", "tail"], new ProcessResult(0,
                "2026-11-21T10:00:01.000000+00:00 api/api/0123456789abcdef shown\n" +
                "2026-11-21T10:00:05.000000+00:00 api/api/aaaaaaaabbbbbbbb new task line\n", ""));

        var lines = await Target(aws).ReadLogsAsync(EcsFixtures.EcsConfig(), new ServiceLogQuery { Service = "api", Follow = true }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text)).ShouldBe([("01234567", "shown"), ("aaaaaaaa", "new task line")]);
        var tail = aws.Single("logs", "tail");
        tail.Args.ShouldContain("--follow");
        tail.Args[tail.Args.ToList().IndexOf("--log-stream-name-prefix") + 1].ShouldBe("api/api/");
        tail.Args[tail.Args.ToList().IndexOf("--since") + 1].ShouldBe("2026-11-21T10:00:01.000Z");
    }

    [Fact]
    public async Task AnUnknownService_SaysSo()
    {
        var aws = new FakeProcessRunner().When(["ecs", "list-tasks"],
            new ProcessResult(254, "", "An error occurred (ServiceNotFoundException) when calling the ListTasks operation: Service not found."));

        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Target(aws).ReadLogsAsync(EcsFixtures.EcsConfig(), new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no service 'nope'");
    }
}
