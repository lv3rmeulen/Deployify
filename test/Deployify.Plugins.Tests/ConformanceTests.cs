using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Testing;
using Deployify.Target.Compose;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

public sealed class ConformanceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _output = Directory.CreateTempSubdirectory("deployify-conformance-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(
        """
        {
          "resources": {
            "api": { "type": "container.v0", "image": "nginx:1.27", "env": { "DB_PASSWORD": "{db-password.value}" } },
            "db-password": { "type": "parameter.v0", "value": "{db-password.inputs.value}",
                             "inputs": { "value": { "type": "string", "secret": true } } }
          }
        }
        """)!;

    private ConformanceOptions Options(bool live = false) => new()
    {
        Manifest = Manifest(),
        Config = new EnvironmentConfig { TargetConfig = { ["compose"] = JsonSerializer.SerializeToElement(new { ProjectName = "c" }) } },
        OutputDirectory = _output,
        Live = live,
        Destructive = live,
        ObserveBudget = TimeSpan.FromSeconds(1)
    };

    [Fact]
    public async Task AWellBehavedTarget_Passes()
    {
        var report = await PluginConformance.RunInProcessAsync(
            p => p.Named("good", "1.0.0").AddTarget<GoodTarget>("good", "Good"), "good", Options(live: true), Ct);

        report.Passed.ShouldBeTrue(report.ToString());
        report.Checks.ShouldNotContain(c => c.Outcome == CheckOutcome.Skipped, report.ToString());
        Should.NotThrow(report.ThrowIfFailed);
    }

    [Fact]
    public async Task ATargetThatBreaksTheContracts_FailsEachCheck()
    {
        var report = await PluginConformance.RunInProcessAsync(
            p => p.Named("bad").AddTarget<BadTarget>("bad", "Bad"), "bad", Options(live: true), Ct);

        report.Passed.ShouldBeFalse();
        var failed = report.Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => c.Name).ToList();
        failed.ShouldBe(
        [
            "explain hides secret values",
            "dependencies name known services",
            "generate writes the files it returns",
            "plan and diff answer",
            "snapshots hold no secret values",
            "observe takes one look",
            "deleting a missing service succeeds"
        ], ignoreOrder: true, report.ToString());
        Should.Throw<ConformanceException>(report.ThrowIfFailed).Message.ShouldContain("✗ observe takes one look");
    }

    [Fact]
    public async Task TheBuiltInComposeTarget_PassesTheOfflineChecksAsAPlugin()
    {
        var report = await PluginConformance.RunInProcessAsync(
            p => p.Named("compose-plugin", "1.0.0")
                .AddTarget<ComposeDeploymentTarget>("compose-plugin", "Compose", t => t.ConfigKey = "compose"),
            "compose-plugin", Options(), Ct);

        report.Passed.ShouldBeTrue(report.ToString());
        report.Checks.Single(c => c.Name == "live checks").Outcome.ShouldBe(CheckOutcome.Skipped);
    }

    [Fact]
    public async Task AnUnstartablePlugin_FailsTheHandshake()
    {
        var report = await PluginConformance.RunAsync(Path.Combine(_output, "nope"), "example", Options(), Ct);

        report.Checks.ShouldHaveSingleItem().Name.ShouldBe("handshake");
        report.Passed.ShouldBeFalse();
    }
}

internal abstract class ConformanceTargetBase : IDeploymentTarget, IRolloutTarget, IResourceLifecycle
{
    public abstract DeploymentTargetType TargetType { get; }

    public string DisplayName => GetType().Name;

    protected static async Task<string> SecretAsync(AspireManifest manifest, EnvironmentConfig config)
        => await ParameterResolution.For(config).GetValueAsync("db-password", ParameterInput.ReadAll(manifest.Resources["db-password"])["value"], TestContext.Current.CancellationToken) ?? "";

    public virtual Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((true, null));

    public virtual async Task<IReadOnlyList<string>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var file = Path.Combine(outputDirectory, "api.yml");
        await File.WriteAllTextAsync(file, "image: nginx", ct);
        return [file];
    }

    public Task<IReadOnlyList<ValidationResult>> ValidateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ValidationResult>>([new ValidationResult { ArtifactName = "api.yml" }]);

    public Task<PlanResult> PlanAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult(new PlanResult { ToCreate = ["api"] });

    public Task<IReadOnlyList<DeploymentResult>> ApplyAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeploymentResult>>([]);

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceExplanation>>([new ServiceExplanation { Name = "api", Env = { ["DB_PASSWORD"] = "****" } }]);

    public virtual Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceDependency>>([]);

    public virtual Task<IReadOnlyList<DiffEntry>> DiffAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiffEntry>>([new DiffEntry { ServiceName = "api", Kind = DiffKind.Added }]);

    public virtual Task<ServiceSnapshot> CaptureAsync(AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => Task.FromResult(new ServiceSnapshot { ServiceName = service, Existed = true, Revision = "3" });

    public Task<AppliedRevision> ApplyServiceAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
        => Task.FromResult(new AppliedRevision { ServiceName = service, Success = true, Revision = "4" });

    public virtual Task<HealthObservation> ObserveAsync(EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => Task.FromResult(HealthObservation.Healthy());

    public Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
        => Task.FromResult(snapshot.Revision);

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ManagedService>>([]);

    public virtual Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class GoodTarget : ConformanceTargetBase
{
    public override DeploymentTargetType TargetType => DeploymentTargetType.Parse("good");
}

/// <summary>Breaks one contract per check.</summary>
internal sealed class BadTarget : ConformanceTargetBase
{
    private readonly HashSet<string> _deleted = [];

    public override DeploymentTargetType TargetType => DeploymentTargetType.Parse("bad");

    public override Task<IReadOnlyList<string>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([Path.Combine(outputDirectory, "never-written.yml")]);

    public override async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => [new ServiceExplanation { Name = "api", Env = { ["DB_PASSWORD"] = await SecretAsync(manifest, config) } }];

    public override Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceDependency>>([new ServiceDependency { From = "api", To = "ghost" }]);

    public override Task<IReadOnlyList<DiffEntry>> DiffAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiffEntry>>([new DiffEntry { ServiceName = "api", Kind = DiffKind.Unknown }]);

    public override async Task<ServiceSnapshot> CaptureAsync(AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => new ServiceSnapshot { ServiceName = service, Existed = true, Revision = "3", Data = { ["env"] = await SecretAsync(manifest, config) } };

    public override async Task<HealthObservation> ObserveAsync(EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return HealthObservation.Healthy();
    }

    public override Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
        => _deleted.Add(service) ? Task.CompletedTask : throw new InvalidOperationException($"{service} is already gone");
}
