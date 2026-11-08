using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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

public sealed class ComposeRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Old = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string New = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static ComposeDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<ComposeDeploymentTarget>.Instance) { Processes = fake };

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    private static ComposeContainerStatus Container(string image, string status = "running", string? health = null,
        int restarts = 0, int exitCode = 0)
        => new("c1", image, status, health, restarts, exitCode, null);

    private static string Inspect(string image, string? configFiles = null, string status = "running")
    {
        var labels = new JsonObject { ["com.docker.compose.service"] = "api" };
        if (configFiles is not null)
        {
            labels["com.docker.compose.project.config_files"] = configFiles;
        }

        return new JsonArray(new JsonObject
        {
            ["Id"] = "c1",
            ["Image"] = image,
            ["RestartCount"] = 0,
            ["State"] = new JsonObject { ["Status"] = status, ["ExitCode"] = 0 },
            ["Config"] = new JsonObject { ["Labels"] = labels }
        }).ToJsonString();
    }

    // ── Health evaluation ────────────────────────────────────────────────────

    [Fact]
    public void Running_WithoutHealthcheck_IsHealthy()
    {
        var obs = ComposeRolloutHealth.Evaluate([Container(New)], New);

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("1 container(s) running (no healthcheck)");
    }

    [Fact]
    public void HealthcheckStarting_IsProgressing_ThenHealthy()
    {
        ComposeRolloutHealth.Evaluate([Container(New, health: "starting")], New).State.ShouldBe(HealthState.Progressing);
        ComposeRolloutHealth.Evaluate([Container(New, health: "healthy")], New).State.ShouldBe(HealthState.Healthy);
    }

    [Theory]
    [InlineData("exited", null, 0)]
    [InlineData("running", "unhealthy", 0)]
    [InlineData("restarting", null, 3)]
    public void CrashedOrUnhealthy_IsFailed(string status, string? health, int restarts)
        => ComposeRolloutHealth.Evaluate([Container(New, status, health, restarts, exitCode: 1)], New)
            .State.ShouldBe(HealthState.Failed);

    [Fact]
    public void OtherImage_IsFailed()
        => ComposeRolloutHealth.Evaluate([Container(Old)], New).State.ShouldBe(HealthState.Failed);

    [Fact]
    public void OldContainerStillRunning_IsProgressing()
        => ComposeRolloutHealth.Evaluate([Container(New), Container(Old)], New).State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void InspectOutputIsParsed()
    {
        var c = ComposeContainerStatus.ParseInspect(Inspect(New, "/app/compose.yml")).Single();

        c.ImageId.ShouldBe(New);
        c.Status.ShouldBe("running");
        c.ConfigFiles.ShouldBe("/app/compose.yml");
    }

    // ── Capture / restore through docker ─────────────────────────────────────

    [Fact]
    public async Task Capture_RecordsImageIdAndComposeFiles()
    {
        var fake = new FakeProcessRunner()
            .When(["ps", "-a", "-q"], Ok("c1\n"))
            .When(["inspect", "c1"], Ok(Inspect(Old, "/app/compose.yml")));

        var snap = await Target(fake).CaptureAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), "api", Ct);

        snap.Existed.ShouldBeTrue();
        snap.Revision.ShouldBe(Old);
        snap.Data["configFiles"].ShouldBe("/app/compose.yml");
        fake.Single("ps", "-a", "-q").Args.ShouldContain("label=com.docker.compose.project=testproject");
    }

    [Fact]
    public async Task Capture_NoContainers_IsNew()
    {
        var fake = new FakeProcessRunner().When(["ps", "-a", "-q"], Ok(""));

        var snap = await Target(fake).CaptureAsync(Fixtures.SingleService("api"), Fixtures.ComposeConfig(), "api", Ct);

        snap.Existed.ShouldBeFalse();
    }

    [Fact]
    public async Task Restore_PinsThePreviousImage_ThroughAnOverrideFile()
    {
        using var dir = new TempDir();
        var composeFile = Path.Combine(dir.Path, "compose.yml");
        await File.WriteAllTextAsync(composeFile, "services: {}", Ct);
        var fake = new FakeProcessRunner()
            .When(["ps", "-a", "-q"], Ok("c1\n"))
            .When(["inspect", "c1"], Ok(Inspect(New, composeFile)));
        var snapshot = new ServiceSnapshot
        {
            ServiceName = "api", Existed = true, Revision = Old, Data = new() { ["configFiles"] = composeFile }
        };

        var waitFor = await Target(fake).RestoreAsync(Fixtures.ComposeConfig(), snapshot, Ct);

        waitFor.ShouldBe(Old);
        var overridePath = Path.Combine(dir.Path, "rollback.api.override.yml");
        var pinned = await File.ReadAllTextAsync(overridePath, Ct);
        pinned.ShouldContain($"image: \"{Old}\"");
        pinned.ShouldContain("pull_policy: never");
        var up = fake.Single("up", "-d", "--no-deps", "api").Args.ToList();
        up[up.IndexOf("-f") + 1].ShouldBe(composeFile);
        up[up.LastIndexOf("-f") + 1].ShouldBe(overridePath);
    }

    [Fact]
    public async Task Restore_UnchangedImage_DoesNothing()
    {
        var fake = new FakeProcessRunner()
            .When(["ps", "-a", "-q"], Ok("c1\n"))
            .When(["inspect", "c1"], Ok(Inspect(Old)));

        var waitFor = await Target(fake).RestoreAsync(Fixtures.ComposeConfig(),
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = Old }, Ct);

        waitFor.ShouldBe(Old);
        fake.Calls.ShouldNotContain(c => c.Args.Contains("up"));
    }

    [Fact]
    public async Task Restore_NewService_RemovesItsContainers()
    {
        var fake = new FakeProcessRunner()
            .When(["ps", "-a", "-q"], Ok("c1\n"))
            .When(["inspect", "c1"], Ok(Inspect(New)));

        var waitFor = await Target(fake).RestoreAsync(Fixtures.ComposeConfig(),
            new ServiceSnapshot { ServiceName = "api", Existed = false }, Ct);

        waitFor.ShouldBeNull();
        fake.Single("rm", "-f").Args.ShouldContain("c1");
    }

    [Fact]
    public async Task Restore_WithoutTheOriginalComposeFile_Throws()
    {
        var fake = new FakeProcessRunner()
            .When(["ps", "-a", "-q"], Ok("c1\n"))
            .When(["inspect", "c1"], Ok(Inspect(New)));

        await Should.ThrowAsync<System.InvalidOperationException>(() => Target(fake).RestoreAsync(Fixtures.ComposeConfig(),
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = Old }, Ct));
    }
}
