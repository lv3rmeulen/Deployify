using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Target.Iis.Tests;

/// <summary>How the manifest becomes IIS sites.</summary>
public sealed class IisSiteGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(IisSiteGenerator Gen, System.Collections.Generic.IReadOnlyList<IisSite> Sites)> Generate(
        System.Action<IisTargetConfig>? configure = null)
    {
        var config = Fixtures.Config(configure);
        var gen = new IisSiteGenerator();
        var sites = await gen.GenerateAsync(Fixtures.Manifest(), config, IisDeploymentTarget.GetConfig(config), ct: Ct);
        return (gen, sites);
    }

    [Fact]
    public async Task Projects_BecomeSites_NamedAfterTheEnvironment()
    {
        var (gen, sites) = await Generate();

        sites.Select(s => s.SiteName).ShouldBe(["prod-api", "prod-web", "prod-worker"]);
        sites.ShouldAllBe(s => s.Environment == "prod" && s.ImageTag == "v1");
        gen.Skipped.Select(s => s.Name).ShouldBe(["cache"]);
    }

    [Fact]
    public async Task SitePrefix_CanBeSet()
    {
        var (_, sites) = await Generate(i => i.SiteNamePrefix = "shop.");

        sites.Select(s => s.SiteName).ShouldContain("shop.api");
    }

    [Fact]
    public async Task Containers_ResolveToTheirExternalHost_AndProjectsToLocalhost()
    {
        var (_, sites) = await Generate();

        sites.Single(s => s.Service == "api").Env["ConnectionStrings__cache"].ShouldBe("redis.internal:6379");
        sites.Single(s => s.Service == "web").Env["services__api__http__0"].ShouldBe("http://localhost:5100");
    }

    [Fact]
    public async Task PortVariables_AreLeftToIis()
    {
        var (_, sites) = await Generate();

        var api = sites.Single(s => s.Service == "api");
        api.Env.ShouldNotContainKey("HTTP_PORTS");
        api.Env["ASPNETCORE_FORWARDEDHEADERS_ENABLED"].ShouldBe("true");
    }

    [Fact]
    public async Task Secrets_StayOutOfSiteJson()
    {
        var (_, sites) = await Generate();
        var api = sites.Single(s => s.Service == "api");
        var dir = Fixtures.TempDir();

        var path = await IisSiteGenerator.WriteSiteFileAsync(api, dir, Ct);

        api.SecretEnv["DB_PASSWORD"].ToString().ShouldBe(Fixtures.Secret);
        var json = await File.ReadAllTextAsync(path, Ct);
        json.ShouldNotContain(Fixtures.Secret);
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("secretKeys")[0].GetString().ShouldBe("DB_PASSWORD");
    }

    [Fact]
    public async Task Https_NeedsACertificate()
    {
        var (_, without) = await Generate();
        var (_, with) = await Generate(i => i.CertificateThumbprint = "AB CD EF");

        without.Single(s => s.Service == "api").Bindings.Select(b => $"{b.Protocol}:{b.Port}").ShouldBe(["http:5100"]);
        var api = with.Single(s => s.Service == "api");
        api.Bindings.Select(b => $"{b.Protocol}:{b.Port}").ShouldBe(["http:5100", "https:5101"]);
        api.CertificateThumbprint.ShouldBe("ABCDEF");
    }

    [Fact]
    public async Task HostName_AddsPort80_And443WithACertificate()
    {
        var (_, sites) = await Generate(i =>
        {
            i.HostNames["web"] = "shop.example.com";
            i.CertificateThumbprint = "ABCD";
        });

        sites.Single(s => s.Service == "web").Bindings.Select(b => $"{b.Protocol}:{b.Port}:{b.HostName}")
            .ShouldBe(["http:5200:", "http:80:shop.example.com", "https:443:shop.example.com"]);
    }

    [Fact]
    public async Task Worker_HasNoBindings()
    {
        var (_, sites) = await Generate();

        sites.Single(s => s.Service == "worker").Bindings.ShouldBeEmpty();
    }

    [Fact]
    public void Script_IsEmbedded_AndAscii()
    {
        IisRemote.Script.ShouldContain("[CmdletBinding()]");
        IisRemote.Script.ShouldAllBe(c => c < 128); // Windows PowerShell 5.1 reads BOM-less files as ANSI
    }
}
