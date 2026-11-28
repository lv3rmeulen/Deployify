using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Target.Host.Tests;

public sealed class HostServiceGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HostService> Service(string name, System.Action<HostTargetConfig>? configure = null)
    {
        var config = Fixtures.Config(configure);
        var services = await new HostServiceGenerator().GenerateAsync(Fixtures.Manifest(), config, HostDeploymentTarget.GetConfig(config), ct: Ct);
        return services.Single(s => s.Service == name);
    }

    [Fact]
    public async Task Projects_BecomeServices_NamedAfterTheEnvironment()
    {
        var config = Fixtures.Config();
        var generator = new HostServiceGenerator();

        var services = await generator.GenerateAsync(Fixtures.Manifest(), config, HostDeploymentTarget.GetConfig(config), ct: Ct);

        services.Select(s => s.Service).ShouldBe(["api", "web", "worker"]);
        services[0].Name.ShouldBe("prod-api");
        services[0].UnitName.ShouldBe("deployify-prod-api");
        generator.Skipped.Single().Name.ShouldBe("cache");
        generator.Skipped.Single().Reason.ShouldContain("redis.internal");
    }

    [Fact]
    public async Task HttpBindings_SetAspNetCoreUrls_AndAspiresPortVariablesAreDropped()
    {
        var api = await Service("api");

        api.Ports.ShouldBe([5100]); // the https binding has no certificate here
        api.Env["ASPNETCORE_URLS"].ShouldBe("http://*:5100");
        api.Env.ShouldNotContainKey("HTTP_PORTS");
    }

    [Fact]
    public async Task ListenAddress_IsUsedInTheUrls()
    {
        var api = await Service("api", h => h.ListenAddress = "localhost");

        api.Env["ASPNETCORE_URLS"].ShouldBe("http://localhost:5100");
    }

    [Fact]
    public async Task References_GoToLocalhost_AndContainersToTheirExternalHost()
    {
        var web = await Service("web");
        var api = await Service("api");

        web.Env["services__api__http__0"].ShouldBe("http://localhost:5100");
        api.Env["ConnectionStrings__cache"].ShouldBe("redis.internal:6379");
        web.DependsOn.ShouldBe(["api"]);
    }

    [Fact]
    public async Task Secrets_StayOutOfThePlainVariables()
    {
        var api = await Service("api");

        api.Env.ShouldNotContainKey("DB_PASSWORD");
        api.SecretKeys.ShouldBe(["DB_PASSWORD"]);
        api.SecretEnv["DB_PASSWORD"].ToString().ShouldBe(Fixtures.Secret);
    }

    [Fact]
    public async Task AWorker_HasNoPorts_AndNoUrls()
    {
        var worker = await Service("worker");

        worker.Ports.ShouldBeEmpty();
        worker.Env.ShouldNotContainKey("ASPNETCORE_URLS");
    }

    [Fact]
    public async Task DotnetRoot_IsSet_ForFrameworkDependentLinuxApps()
    {
        var worker = await Service("worker", h => { h.SelfContained = false; h.DotnetRoot = "/usr/lib/dotnet"; });
        var selfContained = await Service("worker", h => h.DotnetRoot = "/usr/lib/dotnet");

        worker.Env["DOTNET_ROOT"].ShouldBe("/usr/lib/dotnet");
        selfContained.Env.ShouldNotContainKey("DOTNET_ROOT");
    }

    [Fact]
    public async Task KeepReleases_IsAtLeastTwo()
    {
        (await Service("api", h => h.KeepReleases = 1)).KeepReleases.ShouldBe(2);
    }

    [Theory]
    [InlineData("prod-api", "prod-api")]
    [InlineData("my env/api", "my-env-api")]
    public void SafeName_ReplacesWhatServiceManagersRefuse(string name, string expected)
        => HostServiceGenerator.SafeName(name).ShouldBe(expected);

    [Fact]
    public void Defaults_FollowTheOs()
    {
        var linux = new HostTargetConfig { Hosts = ["a"] };
        var windows = new HostTargetConfig { Hosts = ["a"], Os = HostOs.Windows };

        linux.EffectiveOs.ShouldBe(HostOs.Linux);
        linux.EffectiveRoot.ShouldBe("/opt/deployify");
        linux.EffectiveRuntime.ShouldBe("linux-x64");
        windows.EffectiveRoot.ShouldBe(@"C:\deployify");
        windows.EffectiveRuntime.ShouldBe("win-x64");
        windows.EffectiveServiceAccount.ShouldBe(@"NT AUTHORITY\LocalService");
        new HostTargetConfig().Targets.ShouldBe([null]);
    }
}
