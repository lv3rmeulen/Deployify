using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.GitOps.Tests;

public sealed class GitOpsLogsTests
{
    [Fact]
    public async Task Logs_ReadThePodsTheControllerDeployed_WithKubectl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        var kubectl = new FakeProcessRunner().When(["logs"], new ProcessResult(0, "[pod/api-1/api] 2026-11-21T10:00:00Z hello\n", ""));
        var target = Fixtures.Argo(kubectl);

        var lines = await target.ReadLogsAsync(Fixtures.Config(DeploymentTargetType.ArgoCd, dir.Path), new ServiceLogQuery { Service = "api" }, ct)
            .ToListAsync(ct);

        lines.Single().Text.ShouldBe("hello");
        kubectl.Single("logs", "-n", "test-api", "-l", "app=api").FileName.ShouldBe("kubectl");
        kubectl.Calls.ShouldNotContain(c => c.FileName == "git");
    }
}
