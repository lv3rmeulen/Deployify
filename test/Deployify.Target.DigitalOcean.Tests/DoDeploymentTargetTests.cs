using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

public sealed class DoDeploymentTargetTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DoFixtures.TempDir _dir = new();
    private readonly FakeDigitalOceanApi _api = new();
    private readonly RecordingLogger _log = new();
    private readonly IPublisher _publisher = Substitute.For<IPublisher>();

    public void Dispose() => _dir.Dispose();

    private DoDeploymentTarget Target(string? token = "dop_v1_test") => new(_publisher, _log)
    {
        ApiFactory = _ => _api,
        GetVariable = name => name == "DIGITALOCEAN_ACCESS_TOKEN" ? token : null,
        PollInterval = TimeSpan.Zero
    };

    // ── Ping ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_WithoutAToken_SaysWhichVariableToSet()
    {
        var (ok, error) = await Target(token: null).PingAsync(DoFixtures.Config(), Ct);

        ok.ShouldBeFalse();
        error!.ShouldStartWith("Set DIGITALOCEAN_ACCESS_TOKEN");
    }

    [Fact]
    public async Task Ping_PassesOnTheApisAnswer()
    {
        _api.PingError = "DigitalOcean rejected the API token (401).";

        (await Target().PingAsync(DoFixtures.Config(), Ct)).ShouldBe((false, _api.PingError));
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_WithoutAToken_RunsTheLocalChecksOnly()
    {
        var results = await Target(token: null).ValidateAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        results.ShouldHaveSingleItem().IsValid.ShouldBeTrue();
        _api.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_ReportsWhatAppPlatformRejects()
    {
        _api.ProposeError = FakeDigitalOceanApi.Rejection("instance size slug apps-s-9vcpu is not valid");

        var results = await Target().ValidateAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        results.Single().Errors.ShouldHaveSingleItem().ShouldContain("apps-s-9vcpu is not valid");
    }

    // ── Plan ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ForANewApp_CreatesEverything_AndLogsTheCost()
    {
        var plan = await Target().PlanAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        plan.ToCreate.ShouldBe(["api", "cache", "catalog", "orders", "pg"], ignoreOrder: true);
        _api.Calls.ShouldContain("propose new");
        _log.Lines.ShouldContain(l => l.Contains("App Platform estimate for 'deployify-prod': $12.00 per month, not counting Managed Database clusters"));
        _log.Lines.ShouldContain(l => l.Contains("Apply will create Managed Database cluster 'deployify-prod-cache' (valkey, db-s-1vcpu-1gb, 1 node(s))"));
    }

    [Fact]
    public async Task Plan_AfterApply_IsUpToDate()
    {
        var target = Target();
        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        var plan = await target.PlanAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        plan.IsUpToDate.ShouldBeTrue($"to update: {string.Join(", ", plan.ToUpdate)}");
        _api.Calls.ShouldContain(c => c.StartsWith("propose app-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_ReportsAChangedService_Once()
    {
        var target = Target();
        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);
        var config = DoFixtures.Config();
        config.ImageTag = "v1.3.0";
        config.ServiceOverrides["api"] = new ServiceOverride { Env = { ["FEATURE"] = "on" } };

        var plan = await target.PlanAsync(DoFixtures.ShopManifest(), config, _dir.Path, Ct);

        plan.ToUpdate.ShouldBe(["api", "orders"], ignoreOrder: true);
    }

    [Fact]
    public async Task Plan_FailsWhenAppPlatformRejectsTheSpec()
    {
        _api.ProposeError = FakeDigitalOceanApi.Rejection("region xyz is not available");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target().PlanAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct));

        ex.Message.ShouldContain("App Platform rejects the app spec");
    }

    [Fact]
    public async Task Plan_FailsWhenTheAppNameIsTaken()
    {
        _api.Proposal = new DoProposal(12m, NameAvailable: false, NameSuggestion: "deployify-prod-a1b2");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target().PlanAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct));

        ex.Message.ShouldContain("'deployify-prod' is taken");
        ex.Message.ShouldContain("such as 'deployify-prod-a1b2'");
    }

    [Fact]
    public async Task Plan_RefusesASpecWithProblems()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Target().PlanAsync(
            DoFixtures.Single("dash", "mcr.microsoft.com/dotnet/aspire-dashboard:9.4"), DoFixtures.Config(), _dir.Path, Ct));

        ex.Message.ShouldContain("can't be deployed to App Platform as configured");
        _api.Calls.ShouldBeEmpty();
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_CreatesTheCluster_ThenTheApp_ThenTrustsIt_AndWaitsForTheDeployment()
    {
        var results = await Target().ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        var appId = _api.Apps.Keys.Single();
        _api.Calls.ShouldBe(
        [
            "create cluster deployify-prod-cache valkey - ams3 db-s-1vcpu-1gb 1",
            "create app deployify-prod",
            $"trust {appId} on deployify-prod-cache"
        ]);
        _api.Clusters.Values.Single().Tags.ShouldBe(["deployify", "deployify-app-deployify-prod"]);
        results.ShouldAllBe(r => r.Success);
        results.Select(r => r.ServiceName).ShouldBe(["api", "cache", "catalog", "orders", "pg"], ignoreOrder: true);
        _log.Lines.ShouldContain(l => l.Contains("'deployify-prod' is live at https://deployify-prod.ondigitalocean.app"));
    }

    [Fact]
    public async Task Apply_ToAnExistingApp_TrustsItFirst_ThenUpdates()
    {
        var target = Target();
        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);
        _api.Calls.Clear();
        _api.DeploymentPhases = new(["ACTIVE"]);

        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        var appId = _api.Apps.Keys.Single();
        _api.Calls.ShouldBe([$"trust {appId} on deployify-prod-cache", $"update app {appId}"]);
    }

    [Fact]
    public async Task Apply_CreatesMissingDatabasesInAnAttachedPostgresCluster()
    {
        _api.AddCluster("shared-pg", "pg");
        var config = DoFixtures.Config(c => c.Databases["pg"] = new DoDatabaseConfig { Mode = DoDatabaseMode.Cluster, ClusterName = "shared-pg" });

        await Target().ApplyAsync(DoFixtures.ShopManifest(), config, _dir.Path, Ct);

        _api.Calls.ShouldContain("create database appdb in shared-pg");
        _api.Calls.ShouldNotContain(c => c.StartsWith("create cluster shared-pg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_RefusesAClusterOfAnotherEngine()
    {
        _api.AddCluster("deployify-prod-cache", "pg");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target().ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct));

        ex.Message.ShouldContain("runs pg, not valkey");
    }

    [Fact]
    public async Task Apply_NeedsARegionToCreateACluster()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target().ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(c => c.Region = null), _dir.Path, Ct));

        ex.Message.ShouldContain("set TargetConfig.digitalocean.Region");
    }

    [Fact]
    public async Task Apply_ReportsAFailedDeployment()
    {
        _api.DeploymentPhases = new(["BUILDING", "ERROR"]);

        var results = await Target().ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success);
        results.First().Error!.ShouldContain("ended in ERROR");
    }

    [Fact]
    public async Task Apply_GivesUpWaitingAfterTheTimeout()
    {
        _api.DeploymentPhases = new(Enumerable.Repeat("DEPLOYING", 1000));

        var results = await Target().ApplyAsync(
            DoFixtures.ShopManifest(), DoFixtures.Config(c => c.DeployTimeoutMinutes = 0), _dir.Path, Ct);

        results.First().Error!.ShouldContain("was still");
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_NeedsARegistryAppPlatformCanPullFrom()
    {
        var config = DoFixtures.Config();
        config.ContainerRegistry = "myregistry.azurecr.io/shop";

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target().PublishAsync(DoFixtures.ShopManifest(), config, "/apphost", Ct));

        ex.Message.ShouldContain("'myregistry.azurecr.io/shop' is in registry 'myregistry.azurecr.io'");
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Publish_HandsOverToThePublisher()
    {
        var config = DoFixtures.Config();

        await Target().PublishAsync(DoFixtures.ShopManifest(), config, "/apphost", Ct);

        await _publisher.Received(1).PublishAsync(Arg.Any<AspireManifest>(), config, "/apphost", Ct);
    }

    // ── Explain and graph ─────────────────────────────────────────────────────

    [Fact]
    public async Task Explain_DescribesEachComponent_AndMasksSecrets()
    {
        var explanations = (await Target().ExplainAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), Ct)).ToDictionary(e => e.Name);

        explanations["api"].AdditionalInfo["component"].ShouldBe("service, public at /");
        explanations["catalog"].AdditionalInfo["component"].ShouldBe("service, internal only");
        explanations["orders"].AdditionalInfo["component"].ShouldBe("worker");
        explanations["pg"].AdditionalInfo["component"].ShouldBe("dev database (PG 17)");
        explanations["cache"].AdditionalInfo["component"].ShouldBe("Managed Database cluster 'deployify-prod-cache' (VALKEY)");
        explanations["api"].Env["API_KEY"].ShouldBe("****");
    }

    [Fact]
    public async Task Graph_LinksServicesToTheirDatabases()
    {
        var edges = await Target().GetDependenciesAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), Ct);

        edges.Select(e => (e.From, e.To)).ShouldBe(
            [("api", "cache"), ("api", "catalog"), ("api", "pg"), ("orders", "cache"), ("orders", "pg")], ignoreOrder: true);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteService_RemovesTheComponentAndItsRoute()
    {
        var target = Target();
        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        await target.DeleteServiceAsync(DoFixtures.Config(), "api", new DeleteOptions(), Ct);

        var spec = _api.Apps.Values.Single().Spec;
        spec["services"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ShouldBe(["catalog"]);
        spec["ingress"]!["rules"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteService_OfTheLastComponent_DeletesTheApp()
    {
        var target = Target();
        await target.ApplyAsync(DoFixtures.Single("web", "nginx:1.27"), DoFixtures.Config(), _dir.Path, Ct);

        await target.DeleteServiceAsync(DoFixtures.Config(), "web", new DeleteOptions(), Ct);
        await target.DeleteServiceAsync(DoFixtures.Config(), "web", new DeleteOptions(), Ct);

        _api.Apps.ShouldBeEmpty();
        _api.Calls.Count(c => c.StartsWith("delete app", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ListManaged_NamesTheAppsComponents()
    {
        var target = Target();
        (await target.ListManagedAsync(DoFixtures.Config(), Ct)).ShouldBeEmpty();
        await target.ApplyAsync(DoFixtures.ShopManifest(), DoFixtures.Config(), _dir.Path, Ct);

        var managed = await target.ListManagedAsync(DoFixtures.Config(), Ct);

        managed.Select(m => m.Name).ShouldBe(["api", "catalog", "orders", "pg", "cache"], ignoreOrder: true);
    }

    [Fact]
    public async Task Destroy_KeepsClusters_UnlessAskedToRemoveData_AndNeverTouchesOnesItDidntCreate()
    {
        var target = Target();
        _api.AddCluster("shared-pg", "pg");
        var config = DoFixtures.Config(c => c.Databases["pg"] = new DoDatabaseConfig { Mode = DoDatabaseMode.Cluster, ClusterName = "shared-pg" });
        await target.ApplyAsync(DoFixtures.ShopManifest(), config, _dir.Path, Ct);

        var kept = await target.DestroyEnvironmentAsync(config, new DeleteOptions(), Ct);
        kept.ShouldBe(["App Platform app 'deployify-prod' (and its dev databases)"]);
        _api.Clusters.Count.ShouldBe(2);

        var removed = await target.DestroyEnvironmentAsync(config, new DeleteOptions { RemoveVolumes = true }, Ct);
        removed.ShouldBe(["Managed Database cluster 'deployify-prod-cache' and all its data"]);
        _api.Clusters.Values.Select(c => c.Name).ShouldBe(["shared-pg"]);
    }

    private sealed class RecordingLogger : ILogger<DoDeploymentTarget>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
