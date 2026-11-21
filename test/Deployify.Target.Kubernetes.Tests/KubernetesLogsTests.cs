using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary><c>deployify logs</c> on Kubernetes: what kubectl is asked, and how its output is read.</summary>
public sealed class KubernetesLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (KubernetesDeploymentTarget Target, EnvironmentConfig Config) Setup(FakeProcessRunner kubectl)
    {
        var target = new KubernetesDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance)
        {
            Processes = kubectl
        };
        var config = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Kubernetes,
            TargetConfig = new Dictionary<string, object?>
            {
                ["kubernetes"] = JsonSerializer.SerializeToElement(new { namespacePrefix = "shop", context = "prod" })
            }
        };
        return (target, config);
    }

    [Fact]
    public async Task ReadsEveryPodOfTheService_InTimeOrder()
    {
        var kubectl = new FakeProcessRunner().When(["logs"], new ProcessResult(0, """
            [pod/api-7d9-aaaaa/api] 2026-11-21T10:00:01.000000001Z first from a
            [pod/api-7d9-aaaaa/api] 2026-11-21T10:00:03Z third from a
            [pod/api-7d9-bbbbb/api] 2026-11-21T10:00:02Z second from b
            [pod/api-7d9-bbbbb/daprd] 2026-11-21T10:00:04Z sidecar
            """, ""));
        var (target, config) = Setup(kubectl);

        var lines = await target.ReadLogsAsync(config, new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text)).ShouldBe(
        [
            ("api-7d9-aaaaa", "first from a"),
            ("api-7d9-bbbbb", "second from b"),
            ("api-7d9-aaaaa", "third from a"),
            ("api-7d9-bbbbb/daprd", "sidecar")
        ]);
        lines[0].Timestamp.ShouldBe(new DateTimeOffset(2026, 11, 21, 10, 0, 1, TimeSpan.Zero));

        var call = kubectl.Single("logs");
        call.Sensitive.ShouldBeTrue();
        call.Args.ShouldBe(
        [
            "--context", "prod", "logs", "-n", "shop-api", "-l", "app=api", "--all-containers", "--prefix", "--timestamps",
            "--ignore-errors", "--max-log-requests=50", "--tail=100"
        ]);
    }

    [Fact]
    public async Task PassesTailSinceAndFollowOn()
    {
        var kubectl = new FakeProcessRunner();
        var (target, config) = Setup(kubectl);

        await target.ReadLogsAsync(config, new ServiceLogQuery
        {
            Service = "api",
            Tail = null,
            Since = new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero),
            Follow = true
        }, Ct).ToListAsync(Ct);

        var args = kubectl.Single("logs").Args;
        args.ShouldContain("--tail=-1");
        args.ShouldContain("--since-time=2026-11-21T09:30:00Z");
        args.ShouldContain("--follow");
    }

    [Fact]
    public async Task AServiceWithNoPods_SaysSo()
    {
        var kubectl = new FakeProcessRunner().When(["logs"], new ProcessResult(0, "", "No resources found in shop-nope namespace.\n"));
        var (target, config) = Setup(kubectl);

        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await target.ReadLogsAsync(config, new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("'nope' has no pods in namespace shop-nope");
    }

    [Fact]
    public async Task KubectlErrors_AreReported_ButWarningsNextToLogsAreNot()
    {
        var failing = new FakeProcessRunner().When(["logs"], new ProcessResult(1, "", "error: You must be logged in to the server (Unauthorized)\n"));
        var (target, config) = Setup(failing);
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await target.ReadLogsAsync(config, new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("Unauthorized");

        var warning = new FakeProcessRunner().When(["logs"], new ProcessResult(0,
            "[pod/api-1/api] 2026-11-21T10:00:00Z ok\n", "Warning: something deprecated\n"));
        (target, config) = Setup(warning);
        (await target.ReadLogsAsync(config, new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct)).Single().Text.ShouldBe("ok");
    }

    [Fact]
    public async Task WithoutKubectl_SaysWhatIsMissing()
    {
        var kubectl = new FakeProcessRunner();
        kubectl.Missing.Add("kubectl");
        var (target, config) = Setup(kubectl);

        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await target.ReadLogsAsync(config, new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("kubectl");
    }

    [Theory]
    [InlineData("[pod/p1/api] 2026-11-21T10:00:00Z text", "p1", "text")]
    [InlineData("[pod/p1/init-db] 2026-11-21T10:00:00Z text", "p1/init-db", "text")]
    [InlineData("2026-11-21T10:00:00Z no prefix", null, "no prefix")]
    [InlineData("[pod/p1/api] 2026-11-21T10:00:00Z [bracketed] text", "p1", "[bracketed] text")]
    public void Parse_SplitsPrefixAndTime(string line, string? instance, string text)
    {
        var parsed = KubernetesLogs.Parse(line, "api");
        parsed.Instance.ShouldBe(instance);
        parsed.Text.ShouldBe(text);
    }
}
