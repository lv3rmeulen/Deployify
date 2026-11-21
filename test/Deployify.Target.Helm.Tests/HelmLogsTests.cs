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

namespace Deployify.Target.Helm.Tests;

public sealed class HelmLogsTests
{
    [Fact]
    public async Task Logs_ReadTheReleasePods_InTheServiceNamespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var kubectl = new FakeProcessRunner().When(["logs"], new ProcessResult(0, "[pod/api-1/api] 2026-11-21T10:00:00Z hello\n", ""));
        var target = new HelmDeploymentTarget(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, kubectl);

        var lines = await target.ReadLogsAsync(Fixtures.Config(prefix: "shop", context: "prod"), new ServiceLogQuery { Service = "api" }, ct).ToListAsync(ct);

        lines.Single().Text.ShouldBe("hello");
        kubectl.Single("logs", "-n", "shop-api", "-l", "app=api").Args.ShouldContain("prod");
    }
}
