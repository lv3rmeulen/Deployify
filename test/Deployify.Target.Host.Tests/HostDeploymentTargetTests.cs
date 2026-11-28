using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.Host.Tests;

/// <summary>Plan, apply, rollouts, publish, validate, logs and destroy, with the hosts faked.</summary>
public sealed class HostDeploymentTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HostService> Service(string service, EnvironmentConfig config)
    {
        var services = await new HostServiceGenerator().GenerateAsync(Fixtures.Manifest(), config, HostDeploymentTarget.GetConfig(config), ct: Ct);
        return services.Single(s => s.Service == service);
    }

    private static async Task<string> FingerprintOf(string service, EnvironmentConfig config, string packageHash)
        => HostDeploymentTarget.Fingerprint(await Service(service, config), HostDeploymentTarget.GetConfig(config), packageHash,
            char.ToUpperInvariant(service[0]) + service[1..]);

    private static string PublishAll(string output)
    {
        Fixtures.FakePackage(output, "web");
        Fixtures.FakePackage(output, "worker");
        return Fixtures.FakePackage(output, "api");
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ComparesFingerprints_WithWhatTheHostRuns()
    {
        var config = Fixtures.Config();
        var output = Fixtures.TempDir();
        var apiHash = PublishAll(output);
        var web01 = new FakeHost("web01");
        web01.Runs("api", "prod-api", "old", await FingerprintOf("api", config, apiHash));
        web01.Runs("web", "prod-web", "old", "stale");
        web01.Runs("legacy", "prod-legacy", "old", "x");
        web01.Runs("other", "staging-other", "old", "y", env: "staging");

        var plan = await Fixtures.Target(new() { ["web01"] = web01 }).PlanAsync(Fixtures.Manifest(), config, output, Ct);

        plan.Unchanged.ShouldBe(["api"]);
        plan.ToUpdate.ShouldBe(["web"]);
        plan.ToCreate.ShouldBe(["worker"]);
        plan.ToDelete.ShouldBe(["legacy"]); // staging's services are not this environment's
        File.Exists(Path.Combine(output, "deployify-host.sh")).ShouldBeTrue();
        File.Exists(Path.Combine(output, "api", "service.json")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(output, "api", "deployify-prod-api.service")).ShouldContain("ExecStart=/opt/deployify/prod-api/current/run");
        File.ReadAllText(Path.Combine(output, "api", "service.json")).ShouldNotContain(Fixtures.Secret);
    }

    [Fact]
    public async Task Plan_ShowsAnUpdate_WhenOneHostIsMissingTheService()
    {
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var output = Fixtures.TempDir();
        var apiHash = PublishAll(output);
        var web01 = new FakeHost("web01");
        web01.Runs("api", "prod-api", "r1", await FingerprintOf("api", config, apiHash));

        var plan = await Fixtures.Target(new() { ["web01"] = web01 }).DiffAsync(Fixtures.Manifest(), config, output, Ct);

        var api = plan.Single(e => e.ServiceName == "api");
        api.Kind.ShouldBe(DiffKind.Changed);
        api.OldValue.ShouldBe("missing on web02");
    }

    [Fact]
    public async Task Plan_IsUnknown_WhenAHostCantBeReached()
    {
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = new("web01") { Unavailable = "ssh was not found" } };

        var plan = await Fixtures.Target(hosts).PlanAsync(Fixtures.Manifest(), Fixtures.Config(), Fixtures.TempDir(), Ct);

        plan.Unknown.Count.ShouldBe(3);
        plan.Unknown[0].Reason.ShouldContain("ssh was not found");
    }

    [Fact]
    public async Task Fingerprint_ChangesWithTheSecret_ThePackage_AndTheServiceAccount()
    {
        var config = Fixtures.Config();
        var rotated = Fixtures.Config();
        rotated.Parameters["db-password"] = "rotated";
        var windows = Fixtures.Config(h => h.Os = HostOs.Windows);
        var otherAccount = Fixtures.Config(h => { h.Os = HostOs.Windows; h.ServiceAccount = @"CORP\svc-api$"; });

        var a = await FingerprintOf("api", config, "p1");

        (await FingerprintOf("api", config, "p1")).ShouldBe(a);
        (await FingerprintOf("api", rotated, "p1")).ShouldNotBe(a);
        (await FingerprintOf("api", config, "p2")).ShouldNotBe(a);
        (await FingerprintOf("api", otherAccount, "p1")).ShouldNotBe(await FingerprintOf("api", windows, "p1"));
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_SendsTheReleaseWithItsSecrets_ToEveryHost()
    {
        var hosts = new Dictionary<string, FakeHost>();
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var output = Fixtures.TempDir();
        PublishAll(output);

        var results = await Fixtures.Target(hosts).ApplyAsync(Fixtures.Manifest(), config, output, Ct);

        results.ShouldAllBe(r => r.Success);
        results.Single(r => r.ServiceName == "api").DeploymentId.ShouldStartWith("deployify-prod-api@v1-");
        foreach (var host in hosts.Values)
        {
            var api = host.Applied.Single(r => r.Service.Service == "api");
            api.EntryPoint.ShouldBe("Api");
            api.Secrets["DB_PASSWORD"].ShouldBe(Fixtures.Secret);
            api.AllVariables().ShouldContain(kv => kv.Key == "DB_PASSWORD" && kv.Value == Fixtures.Secret);
        }
    }

    [Fact]
    public async Task Apply_StopsBeforeTheNextHost_WhenTheServiceIsUnhealthyOnTheFirst()
    {
        var web01 = new FakeHost("web01") { Health = (s, _) => s.Service == "api" ? HealthObservation.Failed("exited with status 1") : HealthObservation.Healthy() };
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = web01 };
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var output = Fixtures.TempDir();
        PublishAll(output);

        var results = await Fixtures.Target(hosts).ApplyAsync(Fixtures.Manifest(), config, output, Ct);

        var api = results.Single(r => r.ServiceName == "api");
        api.Success.ShouldBeFalse();
        api.Error!.ShouldContain("web01: exited with status 1");
        api.Error!.ShouldContain("stopped before the next host (web02)");
        hosts["web02"].Applied.ShouldNotContain(r => r.Service.Service == "api");
        hosts["web02"].Applied.ShouldContain(r => r.Service.Service == "web");
    }

    [Fact]
    public async Task Apply_WaitsForAHostToBecomeHealthy_ThenGivesUp()
    {
        var web01 = new FakeHost("web01") { Health = (_, _) => HealthObservation.Progressing("no response yet") };
        var config = Fixtures.Config(h => { h.Hosts = ["web01", "web02"]; h.HostHealthTimeoutSeconds = 1; });
        var output = Fixtures.TempDir();
        PublishAll(output);

        var results = await Fixtures.Target(new() { ["web01"] = web01 }).ApplyAsync(Fixtures.Manifest(), config, output, Ct);

        results.First().Error!.ShouldContain("not healthy after 1s (no response yet)");
    }

    [Fact]
    public async Task Apply_WithOneHost_DoesntWaitForHealth()
    {
        var web01 = new FakeHost("web01");
        var output = Fixtures.TempDir();
        PublishAll(output);

        await Fixtures.Target(new() { ["web01"] = web01 }).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), output, Ct);

        web01.Log.ShouldNotContain(l => l.StartsWith("health", StringComparison.Ordinal)); // the rollout's health gate does that
    }

    [Fact]
    public async Task Apply_Fails_ForAServiceThatWasNotPublished()
    {
        var output = Fixtures.TempDir();
        Fixtures.FakePackage(output, "api");

        var results = await Fixtures.Target([]).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), output, Ct);

        results.Single(r => r.ServiceName == "web").Error!.ShouldContain("Run 'deployify publish' first");
        results.Single(r => r.ServiceName == "api").Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_ReportsAnUnreachableHost_ForEveryService()
    {
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = new("web01") { Unavailable = "ssh was not found" } };

        var results = await Fixtures.Target(hosts).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), Fixtures.TempDir(), Ct);

        results.Count.ShouldBe(3);
        results.ShouldAllBe(r => !r.Success && r.Error == "ssh was not found");
    }

    // ── Rollouts ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rollout_CapturesEachHostsRelease_AndRestoresIt()
    {
        var web01 = new FakeHost("web01");
        var web02 = new FakeHost("web02");
        web01.Runs("api", "prod-api", "r1", "fp-r1");
        web02.Runs("api", "prod-api", "r1", "fp-r1");
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = web01, ["web02"] = web02 };
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var target = Fixtures.Target(hosts);
        var output = Fixtures.TempDir();
        PublishAll(output);

        var snapshot = await target.CaptureAsync(Fixtures.Manifest(), config, "api", Ct);
        var applied = await target.ApplyServiceAsync(Fixtures.Manifest(), config, output, "api", Ct);
        var restored = await target.RestoreAsync(config, snapshot, Ct);

        snapshot.Existed.ShouldBeTrue();
        snapshot.Revision.ShouldBe("r1");
        snapshot.Data.ShouldContainKeyAndValue("release@web02", "r1");
        applied.Success.ShouldBeTrue();
        applied.Revision!.ShouldStartWith("v1-");
        restored.ShouldBe("r1");
        web01.Services["prod-api"].Release.ShouldBe("r1");
        web02.Log.ShouldContain("activate prod-api r1");
    }

    [Fact]
    public async Task Restore_RemovesAServiceTheRolloutCreated()
    {
        var web01 = new FakeHost("web01");
        var target = Fixtures.Target(new() { ["web01"] = web01 });
        var config = Fixtures.Config();
        var output = Fixtures.TempDir();
        PublishAll(output);

        var snapshot = await target.CaptureAsync(Fixtures.Manifest(), config, "worker", Ct);
        await target.ApplyServiceAsync(Fixtures.Manifest(), config, output, "worker", Ct);
        var restored = await target.RestoreAsync(config, snapshot, Ct);

        snapshot.Existed.ShouldBeFalse();
        restored.ShouldBeNull();
        web01.Services.ShouldNotContainKey("prod-worker");
    }

    [Fact]
    public async Task Observe_IsHealthyOnlyWhenEveryHostIs_AndFailsAsSoonAsOneFails()
    {
        var web01 = new FakeHost("web01");
        var web02 = new FakeHost("web02") { Health = (_, _) => HealthObservation.Progressing("starting") };
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = web01, ["web02"] = web02 };
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var target = Fixtures.Target(hosts);
        await target.GenerateAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct);

        (await target.ObserveAsync(config, "api", "r2", Ct)).State.ShouldBe(HealthState.Progressing);

        web02.Health = (s, _) => s.Ports.SequenceEqual([5100]) ? HealthObservation.Healthy() : HealthObservation.Failed("no port");
        (await target.ObserveAsync(config, "api", "r2", Ct)).State.ShouldBe(HealthState.Healthy);

        web01.Health = (_, _) => HealthObservation.Failed("exited");
        var failed = await target.ObserveAsync(config, "api", "r2", Ct);
        failed.State.ShouldBe(HealthState.Failed);
        failed.Detail.ShouldBe("exited");
    }

    // ── Publish ──────────────────────────────────────────────────────────────

    private static FakeProcessRunner Dotnet(params string[] files)
        => new FakeProcessRunner().When(spec => spec.FileName == "dotnet", spec =>
        {
            var dir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "App.runtimeconfig.json"), "{}");
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(dir, file), "x");
            }

            return new ProcessResult(0, "", "");
        });

    [Fact]
    public async Task Publish_PublishesEveryProject_SelfContainedForTheHostsRuntime()
    {
        var fake = Dotnet();
        var appHost = Fixtures.TempDir();
        var config = Fixtures.Config();

        await Fixtures.Target([], fake).PublishAsync(Fixtures.Manifest(), config, appHost, Ct);

        var api = fake.Calls.Single(c => c.Args.Any(a => a.EndsWith("Api.csproj", StringComparison.Ordinal)));
        api.Args.ShouldContain("linux-x64");
        api.Args[api.Args.ToList().IndexOf("--self-contained") + 1].ShouldBe("true");
        FolderPackage.ReadPackageHash(Path.Combine(appHost, config.OutputPath), "api").ShouldNotBeNull();
    }

    [Fact]
    public async Task Publish_RefusesAProject_ThatCantRunAsAWindowsService()
    {
        var config = Fixtures.Config(h => h.Os = HostOs.Windows);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Fixtures.Target([], Dotnet()).PublishAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct));

        ex.Message.ShouldContain("can't run as a Windows service");
        ex.Message.ShouldContain("Microsoft.Extensions.Hosting.WindowsServices");
        ex.Message.ShouldContain("Api.csproj");
    }

    [Fact]
    public async Task Publish_AcceptsAWindowsServiceProject()
    {
        var fake = Dotnet("Microsoft.Extensions.Hosting.WindowsServices.dll");
        var config = Fixtures.Config(h => h.Os = HostOs.Windows);

        await Fixtures.Target([], fake).PublishAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct);

        fake.Calls.ShouldAllBe(c => c.Args.Contains("win-x64"));
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_FlagsContainersWithoutAnExternalHost_AndAMismatchedRuntime()
    {
        var config = Fixtures.Config(h => { h.ExternalHosts.Clear(); h.Runtime = "win-x64"; });

        var results = await Fixtures.Target([]).ValidateAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct);

        results.Single(r => r.ArtifactName == "cache").Errors.Single().ShouldContain("host.externalHosts.cache");
        results.Single(r => r.ArtifactName == "host.runtime").Errors.Single().ShouldContain("doesn't run on Linux");
        results.SelectMany(r => r.Errors).ShouldNotContain(e => e.Contains("environment variable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_FlagsRepeatedHosts()
    {
        var config = Fixtures.Config(h => h.Hosts = ["web01", "WEB01"]);

        var results = await Fixtures.Target([]).ValidateAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct);

        results.Single(r => r.ArtifactName == "host.hosts").IsValid.ShouldBeFalse();
    }

    // ── Ping ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_ReportsEachHostsProblems()
    {
        var web02 = new FakeHost("web02");
        web02.Problems.Add("systemd (systemctl) was not found");
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = new("web01"), ["web02"] = web02 };

        var (ok, error) = await Fixtures.Target(hosts).PingAsync(Fixtures.Config(h => h.Hosts = ["web01", "web02"]), Ct);

        ok.ShouldBeFalse();
        error.ShouldBe("web02: systemd (systemctl) was not found.");
    }

    // ── Logs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logs_ReadsEveryHostThatRunsTheService()
    {
        var t0 = DateTimeOffset.Parse("2026-11-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var web01 = new FakeHost("web01");
        var web02 = new FakeHost("web02");
        web01.Runs("api", "prod-api", "r1", "f");
        web02.Runs("api", "prod-api", "r1", "f");
        web01.Lines.Add(new ServiceLogLine { Text = "second", Instance = "web01", Timestamp = t0.AddSeconds(2) });
        web02.Lines.Add(new ServiceLogLine { Text = "first", Instance = "web02", Timestamp = t0 });
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = web01, ["web02"] = web02 };

        var lines = await Fixtures.Target(hosts)
            .ReadLogsAsync(Fixtures.Config(h => h.Hosts = ["web01", "web02"]), new ServiceLogQuery { Service = "api" }, Ct)
            .ToListAsync(Ct);

        lines.Select(l => l.Text).ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Logs_ForAServiceThatIsntDeployed_SaysSo()
    {
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Fixtures.Target([]).ReadLogsAsync(Fixtures.Config(), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct));

        ex.Message.ShouldBe("api is not deployed to web01.");
    }

    // ── Destroy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Destroy_ListsEveryHostsServices_AndDeletesThemWhereTheyAre()
    {
        var web01 = new FakeHost("web01");
        var web02 = new FakeHost("web02");
        web01.Runs("api", "prod-api", "r1", "f");
        web02.Runs("api", "prod-api", "r1", "f");
        web02.Runs("web", "prod-web", "r1", "f");
        var hosts = new Dictionary<string, FakeHost> { ["web01"] = web01, ["web02"] = web02 };
        var config = Fixtures.Config(h => h.Hosts = ["web01", "web02"]);
        var target = Fixtures.Target(hosts);

        var managed = await target.ListManagedAsync(config, Ct);
        await target.DeleteServiceAsync(config, "web", new DeleteOptions(), Ct);

        managed.Select(m => m.Name).ShouldBe(["api", "web"]);
        web01.Log.ShouldNotContain("delete prod-web");
        web02.Log.ShouldContain("delete prod-web");
    }

    [Fact]
    public async Task Explain_DescribesWhereEachServiceRuns()
    {
        var explained = await Fixtures.Target([]).ExplainAsync(Fixtures.Manifest(), Fixtures.Config(), Ct);

        var api = explained.Single(e => e.Name == "api");
        api.Ports.ShouldBe(["http:5100"]);
        api.Env["DB_PASSWORD"].ShouldNotContain(Fixtures.Secret);
        api.AdditionalInfo["systemdUnit"].ShouldBe("deployify-prod-api.service");
        api.AdditionalInfo["folder"].ShouldBe("/opt/deployify/prod-api/releases");
    }
}
