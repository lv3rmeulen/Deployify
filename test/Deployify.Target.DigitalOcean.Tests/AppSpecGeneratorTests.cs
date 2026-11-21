using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

public sealed class AppSpecGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<GeneratedApp> Generate(
        AspireManifest? manifest = null, EnvironmentConfig? config = null, Func<string, string?>? variables = null)
    {
        config ??= DoFixtures.Config();
        return new AppSpecGenerator().GenerateAsync(
            manifest ?? DoFixtures.ShopManifest(), config, DoTargetConfig.From(config), variables ?? (_ => null), Ct);
    }

    [Fact]
    public async Task Shop_MatchesSnapshot()
    {
        var app = await Generate();

        app.Problems.ShouldBeEmpty();
        Snapshot.Verify("shop/prod.app.json", DoFixtures.Json(app.DisplaySpec) + "\n");
    }

    [Fact]
    public async Task WrittenFile_IsTheMaskedSpec_AndHoldsNoSecret()
    {
        using var dir = new DoFixtures.TempDir();
        var app = await Generate();

        var path = await AppSpecGenerator.WriteAsync(app, dir.Path, Ct);

        Path.GetFileName(path).ShouldBe("deployify-prod.app.json");
        var text = await File.ReadAllTextAsync(path, Ct);
        text.ShouldNotContain(DoFixtures.ApiKey);
        text.ShouldNotContain(DoFixtures.PgPassword);
        text.ShouldNotContain(DoFixtures.CachePassword);
        text.ShouldContain("\"key\": \"API_KEY\"");
    }

    [Fact]
    public async Task SecretParameters_BecomeSecretEnvs_WithTheRealValueOnlyInTheSpecToSend()
    {
        var app = await Generate();

        var sent = DoFixtures.AllEnvs(app.Spec).Single(e => e.Key == "API_KEY");
        sent.Type.ShouldBe("SECRET");
        sent.Value.ShouldBe(DoFixtures.ApiKey);
        DoFixtures.AllEnvs(app.DisplaySpec).Single(e => e.Key == "API_KEY").Value.ShouldBe("****");
    }

    [Fact]
    public async Task PostgresAndRedis_BecomeDatabases_AndConnectionStringsUseBindableVariables()
    {
        var app = await Generate();

        app.Spec.Databases!.Select(d => (d.Name, d.Engine, d.Production, d.ClusterName, d.Version, d.DbName)).ShouldBe(
        [
            ("pg", "PG", (bool?)null, (string?)null, "17", "appdb"),
            ("cache", "VALKEY", true, "deployify-prod-cache", null, null)
        ]);

        var api = app.Spec.Services!.Single(s => s.Name == "api").Envs!.ToDictionary(e => e.Key, e => e.Value);
        api["ConnectionStrings__appdb"].ShouldBe(
            "Host=${pg.HOSTNAME};Port=${pg.PORT};Username=${pg.USERNAME};Password=${pg.PASSWORD};Database=appdb;SSL Mode=Require");
        api["ConnectionStrings__cache"].ShouldBe(
            "${cache.HOSTNAME}:${cache.PORT},user=${cache.USERNAME},password=${cache.PASSWORD},ssl=true");

        // The containers' own passwords never reach the app: the platform supplies the real ones.
        DoFixtures.AllEnvs(app.Spec).ShouldNotContain(e => e.Value!.Contains(DoFixtures.PgPassword) || e.Value!.Contains(DoFixtures.CachePassword));
        DoFixtures.AllEnvs(app.Spec).Where(e => e.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal))
            .ShouldAllBe(e => e.Type == "GENERAL");
    }

    [Fact]
    public async Task ReferencesToTheServersHostPortAndPassword_UseBindableVariables()
    {
        var manifest = DoFixtures.ShopManifest();
        manifest.Resources["orders"].Env = new Dictionary<string, string>
        {
            ["DB_HOST"] = "{pg.bindings.tcp.host}",
            ["DB_PORT"] = "{pg.bindings.tcp.port}",
            ["DB_PASSWORD"] = "{pg-password.value}",
            ["REDIS_URL"] = "{cache.bindings.tcp.url}"
        };

        var app = await Generate(manifest);

        var env = app.Spec.Workers!.Single(w => w.Name == "orders").Envs!.ToDictionary(e => e.Key, e => e.Value);
        env["DB_HOST"].ShouldBe("${pg.HOSTNAME}");
        env["DB_PORT"].ShouldBe("${pg.PORT}");
        env["DB_PASSWORD"].ShouldBe("${pg.PASSWORD}");
        env["REDIS_URL"].ShouldBe("rediss://${cache.HOSTNAME}:${cache.PORT}");
    }

    /// <summary>
    /// Aspire 13 adds connection properties for every reference (APPDB_URI, CACHE_URI, …). Built
    /// from a manifest Aspire 13.6.5 wrote, they must all point at the managed databases.
    /// </summary>
    [Fact]
    public async Task Aspire13ConnectionProperties_PointAtTheManagedDatabases()
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<AspireManifest>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "aspire-13.6-postgres-redis.manifest.json"), Ct))!;
        var config = DoFixtures.Config();
        config.Parameters["pg-password"] = "pg-S3cret-e2e";
        config.Parameters["cache-password"] = "cache-S3cret-e2e";

        var app = await Generate(manifest, config);

        app.Problems.ShouldBeEmpty();
        var env = app.Spec.Services!.Single(s => s.Name == "api").Envs!.ToDictionary(e => e.Key, e => e.Value);
        env["ConnectionStrings__appdb"].ShouldBe(
            "Host=${pg.HOSTNAME};Port=${pg.PORT};Username=${pg.USERNAME};Password=${pg.PASSWORD};Database=appdb;SSL Mode=Require");
        env["APPDB_USERNAME"].ShouldBe("${pg.USERNAME}");
        env["APPDB_PASSWORD"].ShouldBe("${pg.PASSWORD}");
        env["APPDB_URI"].ShouldBe("postgresql://${pg.USERNAME}:${pg.PASSWORD}@${pg.HOSTNAME}:${pg.PORT}/appdb");
        env["APPDB_DATABASENAME"].ShouldBe("appdb");
        env["ConnectionStrings__cache"].ShouldBe("${cache.HOSTNAME}:${cache.PORT},user=${cache.USERNAME},password=${cache.PASSWORD},ssl=true");
        env["CACHE_URI"].ShouldBe("rediss://:${cache.PASSWORD}@${cache.HOSTNAME}:${cache.PORT}");
        env.Values.ShouldNotContain(v => v!.Contains("S3cret-e2e"));
        app.Spec.Databases!.Select(d => (d.Name, d.Engine, d.Version)).ShouldBe([("cache", "VALKEY", (string?)null), ("pg", "PG", (string?)"18")]);
    }

    [Fact]
    public async Task AnOverriddenConnectionProperty_IsLeftAlone()
    {
        var manifest = DoFixtures.ShopManifest();
        manifest.Resources["orders"].Env!["APPDB_USERNAME"] = "postgres";
        var config = DoFixtures.Config();
        config.ServiceOverrides["orders"] = new ServiceOverride { Env = { ["APPDB_USERNAME"] = "postgres" } };

        var app = await Generate(manifest, config);

        app.Spec.Workers!.Single().Envs!.Single(e => e.Key == "APPDB_USERNAME").Value.ShouldBe("postgres");
    }

    [Fact]
    public async Task Components_GetTheRightKind()
    {
        var app = await Generate();

        app.Components.ToDictionary(c => c.Resource, c => c.Kind).ShouldBe(new Dictionary<string, ComponentKind>
        {
            ["pg"] = ComponentKind.Database,
            ["cache"] = ComponentKind.Database,
            ["catalog"] = ComponentKind.InternalService,
            ["api"] = ComponentKind.PublicService,
            ["orders"] = ComponentKind.Worker
        }, ignoreOrder: true);

        var api = app.Spec.Services!.Single(s => s.Name == "api");
        api.HttpPort.ShouldBe(8080);
        api.InternalPorts.ShouldBeNull("a project's https binding has nothing listening on it");
        var catalog = app.Spec.Services!.Single(s => s.Name == "catalog");
        catalog.HttpPort.ShouldBeNull();
        catalog.InternalPorts.ShouldBe([9000]);
    }

    [Fact]
    public async Task ServiceUrls_PointAtTheComponentOnThePrivateNetwork()
    {
        var app = await Generate();

        app.Spec.Services!.Single(s => s.Name == "api").Envs!.Single(e => e.Key == "CATALOG_HTTP").Value
            .ShouldBe("http://catalog:9000");
    }

    [Fact]
    public async Task BuiltImages_ComeFromDocr_WithTheEnvironmentsTag_AndOthersFromTheirRegistry()
    {
        var app = await Generate();

        var api = app.Spec.Services!.Single(s => s.Name == "api").Image;
        (api.RegistryType, api.Registry, api.Repository, api.Tag).ShouldBe(("DOCR", (string?)null, "api", (string?)"v1.2.0"));
        var catalog = app.Spec.Services!.Single(s => s.Name == "catalog").Image;
        (catalog.RegistryType, catalog.Registry, catalog.Repository, catalog.Tag).ShouldBe(("GHCR", (string?)"acme", "catalog", (string?)"2.1"));
    }

    [Fact]
    public async Task APublishedDigest_PinsTheImage()
    {
        var config = DoFixtures.Config();
        var digest = "sha256:" + new string('a', 64);
        config.PublishedImages.Record("registry.digitalocean.com/acme/api", "v1.2.0", digest, DateTimeOffset.UtcNow);

        var app = await Generate(config: config);

        var image = app.Spec.Services!.Single(s => s.Name == "api").Image;
        image.Digest.ShouldBe(digest);
        image.Tag.ShouldBeNull();
    }

    [Fact]
    public async Task Settings_SetSizesCountsRoutesAndHealthChecks()
    {
        var config = DoFixtures.Config(c =>
        {
            c.DefaultInstanceSize = "apps-s-1vcpu-1gb";
            c.Services["api"] = new DoServiceConfig { InstanceCount = 3, Route = "shop", HealthCheckPath = "/health" };
        });
        config.ServiceOverrides["orders"] = new ServiceOverride { Count = 2 };

        var app = await Generate(config: config);

        var api = app.Spec.Services!.Single(s => s.Name == "api");
        (api.InstanceSizeSlug, api.InstanceCount, api.HealthCheck?.HttpPath).ShouldBe(("apps-s-1vcpu-1gb", (int?)3, "/health"));
        app.Spec.Workers!.Single().InstanceCount.ShouldBe(2);
        app.Spec.Ingress!.Rules.Single().Match.Path.Prefix.ShouldBe("/shop");
    }

    [Fact]
    public async Task SeveralPublicServices_OneGetsTheRoot_TheOthersTheirName()
    {
        var manifest = DoFixtures.Single("web", "nginx:1.27");
        manifest.Resources["admin"] = DoFixtures.Single("admin", "nginx:1.27").Resources["admin"];
        manifest.Resources["admin"].Bindings!["http"].ContainerPort = 8081;

        var app = await Generate(manifest);

        app.Spec.Ingress!.Rules.Select(r => (r.Component.Name, r.Match.Path.Prefix)).ShouldBe([("admin", "/admin"), ("web", "/")]);
    }

    [Fact]
    public async Task ClusterMode_ForPostgres_AttachesTheNamedCluster()
    {
        var config = DoFixtures.Config(c => c.Databases["pg"] = new DoDatabaseConfig { Mode = DoDatabaseMode.Cluster, ClusterName = "shared-pg" });

        var app = await Generate(config: config);

        var pg = app.Spec.Databases!.Single(d => d.Name == "pg");
        (pg.Production, pg.ClusterName, pg.DbName).ShouldBe(((bool?)true, (string?)"shared-pg", (string?)"appdb"));
    }

    [Fact]
    public async Task ContainerMode_KeepsTheContainer_AndWarnsAboutStorage()
    {
        var manifest = DoFixtures.ShopManifest();
        manifest.Resources["cache"].ExtensionData!["volumes"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new { name = "data", target = "/data" } });
        var config = DoFixtures.Config(c => c.Databases["cache"] = new DoDatabaseConfig { Mode = DoDatabaseMode.Container });

        var app = await Generate(manifest, config);

        app.Spec.Databases!.Select(d => d.Name).ShouldBe(["pg"]);
        app.Spec.Services!.Select(s => s.Name).ShouldContain("cache");
        app.Warnings.ShouldContain(w => w.StartsWith("cache: App Platform has no persistent storage", StringComparison.Ordinal));
        app.Warnings.ShouldContain(w => w.StartsWith("cache: its container args aren't passed on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DevMode_ForRedis_IsAProblem()
    {
        var config = DoFixtures.Config(c => c.Databases["cache"] = new DoDatabaseConfig { Mode = DoDatabaseMode.Dev });

        var app = await Generate(config: config);

        app.Problems.ShouldContain(p => p.StartsWith("cache: App Platform dev databases are Postgres only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADevDatabase_HoldsOneDatabase()
    {
        var manifest = DoFixtures.ShopManifest();
        manifest.Resources["reports"] = new AspireResource { Type = "value.v0", ConnectionString = "{pg.connectionString};Database=reports" };

        var app = await Generate(manifest);

        app.Problems.ShouldContain(p => p.Contains("a dev database holds one database, but the app uses appdb, reports", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnImageFromAnotherRegistry_IsAProblem()
    {
        var app = await Generate(DoFixtures.Single("dash", "mcr.microsoft.com/dotnet/aspire-dashboard:9.4"));

        app.Problems.ShouldHaveSingleItem().ShouldContain("App Platform only pulls from DigitalOcean Container Registry");
    }

    [Fact]
    public async Task AnInvalidAppName_IsAProblem()
    {
        var app = await Generate(config: DoFixtures.Config(c => c.AppName = "Shop_Prod"));

        app.Problems.ShouldContain(p => p.StartsWith("App name 'Shop_Prod' isn't valid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegistryCredentials_AreSentForPrivateBuiltImages_ButMaskedInTheFile()
    {
        var config = DoFixtures.Config(c => c.RegistryCredentialsVariable = "GHCR_PULL");
        config.ContainerRegistry = "ghcr.io/acme";

        var app = await Generate(config: config, variables: v => v == "GHCR_PULL" ? "bot:ghp_token" : null);

        app.Spec.Services!.Single(s => s.Name == "api").Image.RegistryCredentials.ShouldBe("bot:ghp_token");
        app.DisplaySpec.Services!.Single(s => s.Name == "api").Image.RegistryCredentials.ShouldBe("****");
        app.Spec.Services!.Single(s => s.Name == "catalog").Image.RegistryCredentials.ShouldBeNull("only images Deployify built get them");
    }
}
