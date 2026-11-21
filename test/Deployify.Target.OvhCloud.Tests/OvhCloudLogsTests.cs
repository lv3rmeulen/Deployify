using System.IO;
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

namespace Deployify.Target.OvhCloud.Tests;

public sealed class OvhCloudLogsTests
{
    [Fact]
    public async Task Logs_FetchTheKubeconfig_ThenReadThePods()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        var kubeconfig = dir.File("kube", "cluster.kubeconfig");
        var kubectl = new FakeProcessRunner().When(["logs"], new ProcessResult(0, "[pod/api-1/api] 2026-11-21T10:00:00Z hello\n", ""));
        var target = new OvhCloudDeploymentTarget(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, kubectl, FakeOvhApi.Cluster("READY"))
        {
            ReadyPollInterval = System.TimeSpan.Zero
        };

        var lines = await target.ReadLogsAsync(Fixtures.Config(kubeconfig), new ServiceLogQuery { Service = "api" }, ct).ToListAsync(ct);

        lines.Single().Text.ShouldBe("hello");
        File.Exists(kubeconfig).ShouldBeTrue();
        var args = kubectl.Single("logs").Args.ToList();
        args[args.IndexOf("--kubeconfig") + 1].ShouldBe(kubeconfig);
    }
}
