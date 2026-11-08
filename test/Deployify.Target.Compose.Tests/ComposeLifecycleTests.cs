using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Compose.Tests;

/// <summary>What destroy and prune do on Compose: the project label is the ownership marker.</summary>
public sealed class ComposeLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ComposeDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<ComposeDeploymentTarget>.Instance) { Processes = fake };

    [Fact]
    public async Task ListManaged_IsTheProjectsServices_WithDaprSidecarsFoldedIn()
    {
        var fake = new FakeProcessRunner().When(["ps", "-a", "--filter", "label=com.docker.compose.project=testproject"],
            new ProcessResult(0, "api\napi-dapr\nredis\napi\n", ""));

        var managed = await Target(fake).ListManagedAsync(Fixtures.ComposeConfig(), Ct);

        managed.Select(m => m.Name).ShouldBe(["api", "redis"]);
    }

    [Fact]
    public async Task ListManaged_Throws_WhenDockerIsDown()
    {
        var fake = new FakeProcessRunner().When(["ps", "-a", "--filter", "label=com.docker.compose.project=testproject"],
            new ProcessResult(1, "", "Cannot connect to the Docker daemon"));

        await Should.ThrowAsync<InvalidOperationException>(() => Target(fake).ListManagedAsync(Fixtures.ComposeConfig(), Ct));
    }

    [Fact]
    public async Task Delete_RemovesTheServicesContainers_AndItsSidecar()
    {
        var fake = new FakeProcessRunner()
            .When(spec => spec.Args.Contains("label=com.docker.compose.service=api"), _ => new ProcessResult(0, "c1\n", ""))
            .When(spec => spec.Args.Contains("label=com.docker.compose.service=api-dapr"), _ => new ProcessResult(0, "c2\n", ""))
            .When(["inspect"], new ProcessResult(0, """[{"Id":"c1","Image":"i","State":{"Status":"running","ExitCode":0},"RestartCount":0}]""", ""));

        await Target(fake).DeleteServiceAsync(Fixtures.ComposeConfig(), "api", new DeleteOptions(), Ct);

        fake.Calls.Where(c => c.Args.Count > 0 && c.Args[0] == "rm").ShouldNotBeEmpty();
        fake.Calls.Where(c => c.Args.Count > 0 && c.Args[0] == "rm").ShouldAllBe(c => c.Args.Contains("-f"));
    }

    [Fact]
    public async Task DestroyEnvironment_RemovesTheNetwork_ButKeepsVolumesUnlessAsked()
    {
        var fake = new FakeProcessRunner();

        var removed = await Target(fake).DestroyEnvironmentAsync(Fixtures.ComposeConfig(), new DeleteOptions(), Ct);

        removed.ShouldBe(["network testproject"]);
        fake.Calls.ShouldNotContain(c => c.Args.Contains("volume"));
    }

    [Fact]
    public async Task DestroyEnvironment_WithVolumes_RemovesTheProjectsVolumes()
    {
        var fake = new FakeProcessRunner().When(["volume", "ls"], new ProcessResult(0, "testproject_pgdata\n", ""));

        var removed = await Target(fake).DestroyEnvironmentAsync(Fixtures.ComposeConfig(), new DeleteOptions { RemoveVolumes = true }, Ct);

        removed.ShouldBe(["network testproject", "volume testproject_pgdata"]);
        fake.Single("volume", "rm").Args.ShouldContain("testproject_pgdata");
    }
}
