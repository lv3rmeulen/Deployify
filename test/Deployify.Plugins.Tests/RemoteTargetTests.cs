using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Sdk;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>Every target method, through the real SDK server and the real proxy.</summary>
public sealed class RemoteTargetTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InProcessPlugin _plugin = new(p => p
        .Named("fakes", "1.2.3")
        .AddTarget<FullTarget>("full", "Full Fake", t => t.WithConfigSchema<FakeConfig>())
        .AddTarget<PlainTarget>("plain", "Plain Fake"));

    private static readonly AspireManifest Manifest = new()
    {
        Resources =
        {
            ["api"] = new AspireResource { Type = "project.v0", Path = "api.csproj" },
            ["db-password"] = JsonSerializer.Deserialize<AspireResource>(
                """
                { "type": "parameter.v0", "value": "{db-password.inputs.value}",
                  "inputs": { "value": { "type": "string", "secret": true } } }
                """)!
        }
    };

    private static EnvironmentConfig Config() => new()
    {
        Target = DeploymentTargetType.Parse("full"),
        ContainerRegistry = "registry.example.com",
        ImageTag = "v7",
        Parameters = { ["db-password"] = "s3cret" }
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _plugin.DisposeAsync();

    [Fact]
    public async Task Start_ReadsWhatThePluginDeclared()
    {
        var target = _plugin.Target("FULL");
        await target.StartAsync(Ct);

        target.DisplayName.ShouldBe("Full Fake");
        target.Info!.Capabilities.ShouldBe(["rollout", "lifecycle"]);
        target.Info.ConfigSchema!.Value.GetProperty("properties").TryGetProperty("Region", out _).ShouldBeTrue();
        (await _plugin.Host.GetConnectionAsync(Ct)).Info.Plugin.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task Capabilities_FollowTheDeclaration()
    {
        IDeploymentTarget full = _plugin.Target("full");
        IDeploymentTarget plain = _plugin.Target("plain");

        full.GetCapability<IRolloutTarget>().ShouldNotBeNull();
        full.GetCapability<IResourceLifecycle>().ShouldNotBeNull();
        plain.GetCapability<IRolloutTarget>().ShouldBeNull();
        plain.GetCapability<IResourceLifecycle>().ShouldBeNull();
        _plugin.Connections.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Start_FailsForATargetThePluginDoesNotServe()
    {
        var ex = await Should.ThrowAsync<PluginException>(() => _plugin.Target("other").StartAsync(Ct));
        ex.Message.ShouldContain("doesn't serve target 'other'");
        ex.Message.ShouldContain("full, plain");
    }

    [Fact]
    public async Task TargetMethods_RoundTrip()
    {
        var target = _plugin.Target("full");
        var config = Config();

        (await target.PingAsync(config, Ct)).ShouldBe((false, "unreachable registry registry.example.com"));

        var validation = await target.ValidateAsync(Manifest, config, "out", Ct);
        validation.Select(v => (v.ArtifactName, v.IsValid)).ShouldBe([("api.yml", false), ("db.yml", true)]);

        var plan = await target.PlanAsync(Manifest, config, "out", Ct);
        plan.ToCreate.ShouldBe(["api"]);
        plan.Unknown.ShouldBe([new PlanUnknown("legacy", "no access")]);

        var applied = (await target.ApplyAsync(Manifest, config, "out", Ct)).ShouldHaveSingleItem();
        (applied.ServiceName, applied.DeploymentId, applied.Success).ShouldBe(("api", "d-1", true));

        var explained = (await target.ExplainAsync(Manifest, config, Ct)).ShouldHaveSingleItem();
        explained.ImageTag.ShouldBe("v7");
        explained.Env["A"].ShouldBe("1");
        explained.Ports.ShouldBe(["8080"]);

        (await target.GetDependenciesAsync(Manifest, config, Ct)).ShouldHaveSingleItem().To.ShouldBe("db");

        var diff = await target.DiffAsync(Manifest, config, "out", Ct);
        diff.Select(d => d.Kind).ShouldBe([DiffKind.Changed, DiffKind.Unknown]);
        diff[0].NewValue.ShouldBe("a:2");
        diff[1].Reason.ShouldBe("timeout");

        await target.PublishAsync(Manifest, config, "/src/apphost", Ct);
        Recorder.Calls.ShouldContain("publish /src/apphost");
    }

    [Fact]
    public async Task Parameters_AreAskedForAndExplicitValuesAreNotSent()
    {
        var target = _plugin.Target("full");

        var files = await target.GenerateAsync(Manifest, Config(), "out", Ct);

        files.ShouldBe(["out/out.txt", "password=s3cret"]);
        Recorder.LastConfig!.Parameters.ShouldBeEmpty();
        Recorder.LastConfig.ImageTag.ShouldBe("v7");
    }

    [Fact]
    public async Task Parameters_OutsideTheManifestAreRefused()
    {
        var ex = await Should.ThrowAsync<PluginException>(
            () => _plugin.Target("plain").ApplyAsync(Manifest, Config(), "out", Ct));
        ex.Message.ShouldContain("'not-declared' is not a parameter in the manifest");
    }

    [Fact]
    public async Task RolloutMethods_RoundTrip()
    {
        IRolloutTarget rollout = _plugin.Target("full");
        var config = Config();

        var snapshot = await rollout.CaptureAsync(Manifest, config, "api", Ct);
        (snapshot.Existed, snapshot.Revision, snapshot.Data["count"]).ShouldBe((true, "r1", "2"));

        (await rollout.ApplyServiceAsync(Manifest, config, "out", "api", Ct)).Revision.ShouldBe("r2");
        (await rollout.ObserveAsync(config, "api", "r2", Ct)).State.ShouldBe(HealthState.Healthy);
        (await rollout.ObserveAsync(config, "api", "r9", Ct)).ShouldBe(HealthObservation.Reverted("circuit breaker", "r1"));
        (await rollout.RestoreAsync(config, snapshot, Ct)).ShouldBe("r1+2");
        (await rollout.HasNativeRollbackAsync(config, Ct)).ShouldBeTrue();
        (await rollout.GetUnavailableReasonAsync(config, Ct)).ShouldBe("cli missing");
        await rollout.CompleteAsync(Manifest, config, "out", Ct);
        Recorder.Calls.ShouldContain("complete");
    }

    [Fact]
    public async Task LifecycleMethods_RoundTrip()
    {
        IResourceLifecycle lifecycle = _plugin.Target("full");
        var config = Config();

        (await lifecycle.ListManagedAsync(config, Ct)).ShouldBe([new ManagedService("api", "t-1", "prod")]);
        await lifecycle.DeleteServiceAsync(config, "api", new DeleteOptions { KeepSecrets = true }, Ct);
        Recorder.Calls.ShouldContain("delete api keepSecrets=True volumes=False");
        (await lifecycle.DestroyEnvironmentAsync(config, new DeleteOptions(), Ct)).ShouldBe(["network deployify"]);
        (await lifecycle.DescribeKeptAsync(config, new DeleteOptions(), Ct)).ShouldBe(["volumes"]);
    }

    [Fact]
    public async Task TargetErrors_ComeBackAsPluginExceptions()
    {
        var ex = await Should.ThrowAsync<PluginException>(
            () => _plugin.Target("plain").GenerateAsync(Manifest, Config(), "out", Ct));
        ex.Message.ShouldContain("the cluster said no");
    }

    [Fact]
    public async Task Cancelling_CancelsTheCallInThePlugin()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var plan = _plugin.Target("plain").PlanAsync(Manifest, Config(), "out", cts.Token);
        await Task.Delay(200, Ct);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => plan);

        // The connection is still good afterwards.
        (await _plugin.Target("plain").PingAsync(Config(), Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ACrashedPlugin_IsStartedAgain()
    {
        var target = _plugin.Target("full");
        (await target.ExplainAsync(Manifest, Config(), Ct)).ShouldNotBeEmpty();

        _plugin.Kill();
        await Task.Delay(200, Ct);

        (await target.ExplainAsync(Manifest, Config(), Ct)).ShouldNotBeEmpty();
        _plugin.Connections.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneConnection()
    {
        var target = _plugin.Target("full");
        var calls = Enumerable.Range(0, 20).Select(_ => target.ObserveAsync(Config(), "api", "r2", Ct));

        (await Task.WhenAll(calls)).ShouldAllBe(h => h.State == HealthState.Healthy);
        _plugin.Connections.ShouldBe(1);
    }
}
