using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.Iis.Tests;

/// <summary>Plan, apply, publish, validate and destroy, with PowerShell and dotnet faked.</summary>
public sealed class IisDeploymentTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IisSite> Site(string service, EnvironmentConfig config)
    {
        var sites = await new IisSiteGenerator().GenerateAsync(Fixtures.Manifest(), config, IisDeploymentTarget.GetConfig(config), ct: Ct);
        return sites.Single(s => s.Service == service);
    }

    private static object Live(string service, string fingerprint, string env = "prod", bool siteExists = true)
        => new { service, environment = env, siteName = $"{env}-{service}", fingerprint, release = "old", siteExists };

    // ── Plan ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ComparesFingerprints_WithWhatTheServerRuns()
    {
        var config = Fixtures.Config();
        var output = Fixtures.TempDir();
        var apiHash = Fixtures.FakePackage(output, "api");
        Fixtures.FakePackage(output, "web");
        var current = IisDeploymentTarget.Fingerprint(await Site("api", config), apiHash);
        var fake = Fixtures.Server(Live("api", current), Live("web", "stale"), Live("legacy", "x"), Live("other", "y", env: "staging"));

        var plan = await Fixtures.Target(fake).PlanAsync(Fixtures.Manifest(), config, output, Ct);

        plan.Unchanged.ShouldBe(["api"]);
        plan.ToUpdate.ShouldBe(["web"]);
        plan.ToCreate.ShouldBe(["worker"]);
        plan.ToDelete.ShouldBe(["legacy"]); // staging's sites are not this environment's
        File.Exists(Path.Combine(output, "deployify-iis.ps1")).ShouldBeTrue();
        File.Exists(Path.Combine(output, "api", "site.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Plan_IsUnknown_ForADeployedServiceThatWasNotPublishedHere()
    {
        var fake = Fixtures.Server(Live("api", "f"));

        var plan = await Fixtures.Target(fake).PlanAsync(Fixtures.Manifest(), Fixtures.Config(), Fixtures.TempDir(), Ct);

        plan.Unknown.Single().ServiceName.ShouldBe("api");
    }

    [Fact]
    public async Task Plan_IsUnknown_WhenTheServerCantBeReached()
    {
        var fake = new FakeProcessRunner().When(spec => spec.Args.Contains("List"), _ => new ProcessResult(1, "", "WinRM cannot complete the operation"));

        var plan = await Fixtures.Target(fake).PlanAsync(Fixtures.Manifest(), Fixtures.Config(), Fixtures.TempDir(), Ct);

        plan.Unknown.Count.ShouldBe(3);
        plan.Unknown[0].Reason.ShouldContain("WinRM cannot complete the operation");
    }

    [Fact]
    public async Task Fingerprint_ChangesWithTheSecret_AndThePackage()
    {
        var config = Fixtures.Config();
        var site = await Site("api", config);
        var other = Fixtures.Config();
        other.Parameters["db-password"] = "rotated";

        var a = IisDeploymentTarget.Fingerprint(site, "p1");
        IisDeploymentTarget.Fingerprint(await Site("api", other), "p1").ShouldNotBe(a);
        IisDeploymentTarget.Fingerprint(site, "p2").ShouldNotBe(a);
        site.ProjectPath = "/elsewhere/Api.csproj";
        IisDeploymentTarget.Fingerprint(site, "p1").ShouldBe(a); // machine paths don't count
    }

    [Fact]
    public void ReleaseName_IsTheTagAndTheFingerprint()
        => IisDeploymentTarget.ReleaseName(new IisSite { ImageTag = "1.2/rc:1" }, "0123456789abcdef")
            .ShouldBe("1.2_rc_1-0123456789ab");

    // ── Apply ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_SendsSecretsOnStdin_NeverAsArguments()
    {
        var output = Fixtures.TempDir();
        Fixtures.FakePackage(output, "api");
        Fixtures.FakePackage(output, "web");
        Fixtures.FakePackage(output, "worker");
        var fake = new FakeProcessRunner().When(
            spec => Fixtures.IsAction(spec, "Apply"),
            _ => new ProcessResult(0, "noise\n##deployify {\"siteName\":\"x\",\"changed\":true}\n", ""));

        var results = await Fixtures.Target(fake).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), output, Ct);

        results.ShouldAllBe(r => r.Success);
        var applies = fake.Calls.Where(c => Fixtures.IsAction(c, "Apply")).ToList();
        applies.Count.ShouldBe(3);
        applies.ShouldAllBe(c => c.Sensitive && !string.Join(" ", c.Args).Contains(Fixtures.Secret));

        var request = JsonDocument.Parse(applies[0].Stdin!).RootElement;
        request.GetProperty("secrets").GetProperty("DB_PASSWORD").GetString().ShouldBe(Fixtures.Secret);
        request.GetProperty("site").GetProperty("siteName").GetString().ShouldBe("prod-api");
        request.GetProperty("site").GetProperty("release").GetString()!.ShouldStartWith("v1-");
        request.GetProperty("connection").GetProperty("host").GetString().ShouldBe("web01");
        request.GetProperty("sitesRoot").GetString().ShouldBe(@"C:\inetpub\deployify");
        results[0].DeploymentId!.ShouldStartWith("web01/prod-api@v1-");
    }

    [Fact]
    public async Task Apply_FailsAService_ThatWasNotPublished()
    {
        var output = Fixtures.TempDir();
        Fixtures.FakePackage(output, "api");
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Apply"), _ => new ProcessResult(0, "##deployify {}", ""));

        var results = await Fixtures.Target(fake).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), output, Ct);

        results.Single(r => r.ServiceName == "api").Success.ShouldBeTrue();
        results.Single(r => r.ServiceName == "web").Error!.ShouldContain("deployify publish");
    }

    [Fact]
    public async Task Apply_ReportsTheServersError()
    {
        var output = Fixtures.TempDir();
        Fixtures.FakePackage(output, "api");
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Apply"),
            _ => new ProcessResult(1, "", "The ASP.NET Core Module is not installed on WEB01."));

        var results = await Fixtures.Target(fake).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), output, Ct);

        results.Single(r => r.ServiceName == "api").Error!.ShouldContain("ASP.NET Core Module");
    }

    [Fact]
    public async Task Apply_NeedsAHost_WhenNotOnWindows()
    {
        Assert.SkipWhen(System.OperatingSystem.IsWindows(), "Deploying to this machine is allowed on Windows.");
        var results = await Fixtures.Target(new FakeProcessRunner())
            .ApplyAsync(Fixtures.Manifest(), Fixtures.Config(i => i.Host = null), Fixtures.TempDir(), Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("iis.host"));
    }

    // ── Publish ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_BuildsEachProjectForWindows_AndRecordsItsHash()
    {
        var appHost = Fixtures.TempDir();
        var fake = new FakeProcessRunner().When(spec => spec.FileName == "dotnet", spec =>
        {
            var dir = spec.Args[spec.Args.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "app.dll"), "x");
            return new ProcessResult(0, "", "");
        });

        await Fixtures.Target(fake).PublishAsync(Fixtures.Manifest(), Fixtures.Config(), appHost, Ct);

        var publish = fake.Calls.Where(c => c.FileName == "dotnet").ToList();
        publish.Count.ShouldBe(3);
        publish[0].Args.ShouldBe(
        [
            "publish", Path.GetFullPath(Path.Combine(appHost, "..", "Api", "Api.csproj")), "-c", "Release", "-r", "win-x64",
            "--self-contained", "false", "-o", Path.Combine(appHost, "deployify-output", "api", "package"),
            "-p:IsTransformWebConfigDisabled=false"
        ]);
        IisPublisher.ReadPackageHash(Path.Combine(appHost, "deployify-output"), "api").ShouldNotBeNull();
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_RejectsWorkers_AndContainersWithNowhereToRun()
    {
        var root = Fixtures.TempDir();
        Directory.CreateDirectory(Path.Combine(root, "Api"));
        File.WriteAllText(Path.Combine(root, "Api", "Api.csproj"), "<Project />");
        var config = Fixtures.Config(i => i.ExternalHosts.Clear());
        config.AppHostPath = Path.Combine(root, "AppHost");

        var results = await Fixtures.Target(new FakeProcessRunner()).ValidateAsync(Fixtures.Manifest(), config, Fixtures.TempDir(), Ct);

        var failed = results.Where(r => !r.IsValid).ToList();
        failed.ShouldContain(r => r.ArtifactName.EndsWith(Path.Combine("worker", "site.json")) && r.Errors[0].Contains("no http endpoint"));
        failed.ShouldContain(r => r.ArtifactName.EndsWith(Path.Combine("web", "site.json")) && r.Errors[0].Contains("project file not found"));
        failed.ShouldContain(r => r.ArtifactName == "cache" && r.Errors[0].Contains("iis.externalHosts.cache"));
        failed.ShouldNotContain(r => r.ArtifactName.EndsWith(Path.Combine("api", "site.json")));
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListManaged_ReportsEveryEnvironment_ForThePlannerToFilter()
    {
        var fake = Fixtures.Server(Live("api", "a"), Live("api", "b", env: "staging"));

        var managed = await Fixtures.Target(fake).ListManagedAsync(Fixtures.Config(), Ct);

        managed.ShouldBe([new ManagedService("api", "prod-api", "prod"), new ManagedService("api", "staging-api", "staging")]);
    }

    [Fact]
    public async Task Delete_RemovesThisEnvironmentsSite()
    {
        var fake = Fixtures.Server(Live("api", "a", env: "staging"), new { service = "api", environment = "prod", siteName = "old-prefix-api" })
            .When(spec => Fixtures.IsAction(spec, "Delete"), _ => new ProcessResult(0, "##deployify {\"removed\":\"site old-prefix-api\"}", ""));

        await Fixtures.Target(fake).DeleteServiceAsync(Fixtures.Config(), "api", new DeleteOptions(), Ct);

        JsonDocument.Parse(Fixtures.Call(fake, "Delete").Stdin!).RootElement.GetProperty("siteName").GetString().ShouldBe("old-prefix-api");
    }

    [Fact]
    public void ParseLive_AcceptsWindowsPowerShellsUnwrappedSingleItem()
    {
        var single = JsonDocument.Parse("""{"services":{"service":"api","siteName":"prod-api","environment":"prod","siteExists":true}}""").RootElement;

        IisDeploymentTarget.ParseLive(single).Single().SiteExists.ShouldBeTrue();
    }

    [Fact]
    public async Task Ping_ExplainsWhatTheServerIsMissing()
    {
        var fake = new FakeProcessRunner().When(spec => Fixtures.IsAction(spec, "Ping"),
            _ => new ProcessResult(0, "##deployify {\"management\":true,\"aspNetCore\":false,\"admin\":true}", ""));

        var (ok, error) = await Fixtures.Target(fake).PingAsync(Fixtures.Config(), Ct);

        ok.ShouldBeFalse();
        error!.ShouldContain("Hosting Bundle");
    }

    [Fact]
    public async Task Shell_FallsBackWhenPwshIsMissing()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("pwsh");
        fake.Missing.Add("powershell");

        var (ok, error) = await Fixtures.Target(fake).PingAsync(Fixtures.Config(), Ct);

        ok.ShouldBeFalse();
        error!.ShouldContain("PowerShell was not found");
    }
}
