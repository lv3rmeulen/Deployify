using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.Iis.Tests;

/// <summary>Health-gated rollouts and rollback: snapshots are release names, restores switch releases.</summary>
public sealed class IisRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Live(string service, string release, string env = "prod", bool siteExists = true, string? siteName = null)
        => new { service, environment = env, siteName = siteName ?? $"{env}-{service}", fingerprint = "f", release, siteExists };

    private static JsonElement Request(FakeProcessRunner fake, string action)
        => JsonDocument.Parse(Fixtures.Call(fake, action).Stdin!).RootElement;

    [Fact]
    public void Target_SupportsRollouts()
        => Fixtures.Target(new FakeProcessRunner()).Supports<IRolloutTarget>().ShouldBeTrue();

    [Fact]
    public async Task Capture_RecordsTheReleaseThisEnvironmentRuns()
    {
        var fake = Fixtures.Server(Live("api", "v1-aaa", env: "staging"), Live("api", "v2-bbb"));

        var snapshot = await Fixtures.Target(fake).CaptureAsync(Fixtures.Manifest(), Fixtures.Config(), "api", Ct);

        snapshot.Existed.ShouldBeTrue();
        snapshot.Revision.ShouldBe("v2-bbb");
        snapshot.Data["siteName"].ShouldBe("prod-api");
        snapshot.Data.Values.ShouldNotContain(v => v.Contains(Fixtures.Secret));
    }

    [Fact]
    public async Task Capture_OfANewService_HasNothingToGoBackTo()
    {
        var fake = Fixtures.Server(Live("api", "v1", siteExists: false));

        var snapshot = await Fixtures.Target(fake).CaptureAsync(Fixtures.Manifest(), Fixtures.Config(), "web", Ct);

        snapshot.Existed.ShouldBeFalse();
        snapshot.Data["siteName"].ShouldBe("prod-web");
    }

    [Fact]
    public async Task ApplyService_DeploysOneSite_AndReturnsItsRelease()
    {
        var output = Fixtures.TempDir();
        Fixtures.FakePackage(output, "api");
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Apply"), _ => new ProcessResult(0, "##deployify {\"changed\":true}", ""));

        var applied = await Fixtures.Target(fake).ApplyServiceAsync(Fixtures.Manifest(), Fixtures.Config(), output, "api", Ct);

        applied.Success.ShouldBeTrue();
        applied.Revision!.ShouldStartWith("v1-");
        Request(fake, "Apply").GetProperty("site").GetProperty("release").GetString().ShouldBe(applied.Revision);
    }

    [Fact]
    public async Task ApplyService_RefusesAContainer()
    {
        var applied = await Fixtures.Target(new FakeProcessRunner())
            .ApplyServiceAsync(Fixtures.Manifest(), Fixtures.Config(), Fixtures.TempDir(), "cache", Ct);

        applied.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("healthy", HealthState.Healthy)]
    [InlineData("failed", HealthState.Failed)]
    [InlineData("progressing", HealthState.Progressing)]
    public async Task Observe_AsksTheServer_ForTheRelease(string state, HealthState expected)
    {
        var fake = Fixtures.Server(Live("api", "v2-bbb"))
            .When(spec => Fixtures.IsAction(spec, "Health"),
                _ => new ProcessResult(0, $"##deployify {{\"state\":\"{state}\",\"detail\":\"HTTP 200\"}}", ""));

        var observation = await Fixtures.Target(fake).ObserveAsync(Fixtures.Config(i => i.HealthPath = "/health"), "api", "v2-bbb", Ct);

        observation.State.ShouldBe(expected);
        var request = Request(fake, "Health");
        request.GetProperty("siteName").GetString().ShouldBe("prod-api");
        request.GetProperty("release").GetString().ShouldBe("v2-bbb");
        request.GetProperty("healthPath").GetString().ShouldBe("/health");
    }

    [Fact]
    public async Task Restore_SwitchesBackToTheRecordedRelease()
    {
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Activate"), _ => new ProcessResult(0, "##deployify {\"changed\":true}", ""));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "v1-aaa", Data = { ["siteName"] = "prod-api" } };

        var waitFor = await Fixtures.Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct);

        waitFor.ShouldBe("v1-aaa");
        var request = Request(fake, "Activate");
        request.GetProperty("siteName").GetString().ShouldBe("prod-api");
        request.GetProperty("release").GetString().ShouldBe("v1-aaa");
        Fixtures.Call(fake, "Activate").Stdin!.ShouldNotContain("secrets"); // the server keeps them
    }

    [Fact]
    public async Task Restore_RemovesASiteTheRolloutCreated()
    {
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Delete"), _ => new ProcessResult(0, "##deployify {\"removed\":[]}", ""));
        var snapshot = new ServiceSnapshot { ServiceName = "web", Existed = false, Data = { ["siteName"] = "prod-web" } };

        var waitFor = await Fixtures.Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct);

        waitFor.ShouldBeNull();
        Request(fake, "Delete").GetProperty("siteName").GetString().ShouldBe("prod-web");
    }

    [Fact]
    public async Task Restore_ReportsAReleaseThatWasPruned()
    {
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Activate"),
            _ => new ProcessResult(1, "", "Release v1-aaa of 'prod-api' is no longer on WEB01 (only the newest iis.keepReleases are kept)."));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "v1-aaa", Data = { ["siteName"] = "prod-api" } };

        var ex = await Should.ThrowAsync<System.InvalidOperationException>(() => Fixtures.Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct));

        ex.Message.ShouldContain("keepReleases");
    }

    [Fact]
    public async Task KeepReleases_NeverDropsBelowTwo()
    {
        var config = Fixtures.Config(i => i.KeepReleases = 1);

        var sites = await new IisSiteGenerator().GenerateAsync(Fixtures.Manifest(), config, IisDeploymentTarget.GetConfig(config), ct: Ct);

        sites.ShouldAllBe(s => s.KeepReleases == 2);
    }

    [Fact]
    public void Script_HasTheRolloutActions()
        => IisRemote.Script.ShouldContain("[ValidateSet('Ping', 'List', 'Apply', 'Activate', 'Health', 'Delete')]");
}

/// <summary>The real rollout engine driving the IIS target, with the server faked.</summary>
public sealed class IisRolloutEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FailedHealth_SwitchesEveryTouchedSiteBack()
    {
        var output = Fixtures.TempDir();
        foreach (var service in new[] { "api", "web" })
        {
            Fixtures.FakePackage(output, service);
        }

        // api ran v0 before; web is new. The new api starts; web's new release fails to start.
        var fake = Fixtures.Server(new { service = "api", environment = "prod", siteName = "prod-api", fingerprint = "old", release = "v0-old", siteExists = true })
            .When(spec => Fixtures.IsAction(spec, "Apply"), _ => new ProcessResult(0, "##deployify {\"changed\":true}", ""))
            .When(spec => Fixtures.IsAction(spec, "Activate"), _ => new ProcessResult(0, "##deployify {\"changed\":true}", ""))
            .When(spec => Fixtures.IsAction(spec, "Delete"), _ => new ProcessResult(0, "##deployify {\"removed\":[]}", ""))
            .When(spec => Fixtures.IsAction(spec, "Health"), spec => new ProcessResult(0, spec.Stdin!.Contains("prod-web")
                ? "##deployify {\"state\":\"failed\",\"detail\":\"the app failed to start (HTTP Error 500.30)\"}"
                : "##deployify {\"state\":\"healthy\",\"detail\":\"HTTP 200\"}", ""));

        // Only the two web apps: the worker can't be hosted by IIS and would fail validation first.
        var manifest = Fixtures.Manifest();
        manifest.Resources.Remove("worker");
        var target = Fixtures.Target(fake);
        var state = Fixtures.TempDir();

        var result = await new Deployify.Core.Rollouts.RolloutEngine().RunAsync(new Deployify.Core.Rollouts.RolloutRequest
        {
            Target = target,
            Rollout = target,
            Manifest = manifest,
            Config = Fixtures.Config(),
            EnvironmentName = "prod",
            OutputDirectory = output,
            StateDirectory = state,
            Options = new Deployify.Core.Rollouts.RolloutOptions
            {
                HealthTimeout = System.TimeSpan.FromSeconds(5),
                PollInterval = System.TimeSpan.FromMilliseconds(1),
                RequiredSuccesses = 1
            }
        }, Ct);

        result.Outcome.ShouldBe(Deployify.Core.Rollouts.RolloutOutcome.RolledBack);
        result.Services.Select(s => s.Name).ShouldBe(["api", "web"]); // cache is not part of the rollout
        JsonDocument.Parse(Fixtures.Call(fake, "Activate").Stdin!).RootElement.GetProperty("release").GetString().ShouldBe("v0-old");
        JsonDocument.Parse(Fixtures.Call(fake, "Delete").Stdin!).RootElement.GetProperty("siteName").GetString().ShouldBe("prod-web");
        (await System.IO.File.ReadAllTextAsync(result.JournalPath, Ct)).ShouldNotContain(Fixtures.Secret);
    }
}
