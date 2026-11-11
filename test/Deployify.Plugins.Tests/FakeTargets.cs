using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;

namespace Deployify.Plugins.Tests;

/// <summary>What the fake targets saw, shared with the test because the plugin builds the target itself.</summary>
internal static class Recorder
{
    public static readonly ConcurrentQueue<string> Calls = new();

    public static EnvironmentConfig? LastConfig;

    public static void Record(string call, EnvironmentConfig config)
    {
        Calls.Enqueue(call);
        LastConfig = config;
    }
}

/// <summary>Supports everything; answers with canned values that show what came across.</summary>
internal sealed class FullTarget : IDeploymentTarget, IRolloutTarget, IResourceLifecycle
{
    public DeploymentTargetType TargetType => DeploymentTargetType.Parse("full");

    public string DisplayName => "Full Fake";

    public Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        Recorder.Record("ping", config);
        return Task.FromResult<(bool, string?)>((false, $"unreachable registry {config.ContainerRegistry}"));
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        Recorder.Record("generate", config);

        // Reads a parameter the way a real target does: through the config's parameter source.
        var resource = manifest.Resources["db-password"];
        var input = ParameterInput.ReadAll(resource)["value"];
        var value = await ParameterResolution.For(config).GetValueAsync("db-password", input, ct);
        return [$"{outputDirectory}/out.txt", $"password={value}"];
    }

    public Task<IReadOnlyList<ValidationResult>> ValidateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ValidationResult>>(
            [new ValidationResult { ArtifactName = "api.yml", Errors = ["bad port"] }, new ValidationResult { ArtifactName = "db.yml" }]);

    public Task<PlanResult> PlanAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult(new PlanResult
        {
            ToCreate = [.. manifest.Resources.Keys.Where(k => k != "db-password")],
            Unknown = [new PlanUnknown("legacy", "no access")]
        });

    public Task<IReadOnlyList<DeploymentResult>> ApplyAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        Recorder.Record("apply", config);
        return Task.FromResult<IReadOnlyList<DeploymentResult>>(
            [new DeploymentResult { ServiceName = "api", DeploymentId = "d-1", Success = true }]);
    }

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
    {
        Recorder.Record($"publish {appHostDirectory}", config);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceExplanation>>([new ServiceExplanation { Name = "api", Image = "api", ImageTag = config.ImageTag, Ports = ["8080"], Env = { ["A"] = "1" } }]);

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceDependency>>([new ServiceDependency { From = "api", To = "db" }]);

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiffEntry>>(
        [
            new DiffEntry { ServiceName = "api", Kind = DiffKind.Changed, Field = "image", OldValue = "a:1", NewValue = "a:2" },
            new DiffEntry { ServiceName = "old", Kind = DiffKind.Unknown, Reason = "timeout" }
        ]);

    public Task<ServiceSnapshot> CaptureAsync(AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => Task.FromResult(new ServiceSnapshot { ServiceName = service, Existed = true, Revision = "r1", Data = { ["count"] = "2" } });

    public Task<AppliedRevision> ApplyServiceAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
        => Task.FromResult(new AppliedRevision { ServiceName = service, Success = true, Revision = "r2" });

    public Task<HealthObservation> ObserveAsync(EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => Task.FromResult(revision == "r2"
            ? HealthObservation.Healthy("2/2 running")
            : HealthObservation.Reverted("circuit breaker", "r1"));

    public Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
        => Task.FromResult<string?>($"{snapshot.Revision}+{snapshot.Data["count"]}");

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<string?>("cli missing");

    public Task CompleteAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        Recorder.Record("complete", config);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ManagedService>>([new ManagedService("api", "t-1", "prod")]);

    public Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        Recorder.Record($"delete {service} keepSecrets={options.KeepSecrets} volumes={options.RemoveVolumes}", config);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> DestroyEnvironmentAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(["network deployify"]);

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(options.RemoveVolumes ? [] : ["volumes"]);
}

/// <summary>Only the required interface; fails or waits on request.</summary>
internal sealed class PlainTarget : IDeploymentTarget
{
    public DeploymentTargetType TargetType => DeploymentTargetType.Parse("plain");

    public string DisplayName => "Plain Fake";

    public Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((true, null));

    public Task<IReadOnlyList<string>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => throw new InvalidOperationException("the cluster said no");

    public Task<IReadOnlyList<ValidationResult>> ValidateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ValidationResult>>([]);

    public async Task<PlanResult> PlanAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new PlanResult();
    }

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        // Asks for a parameter the manifest doesn't declare.
        await ParameterResolution.For(config).GetValueAsync("not-declared", new ParameterInput(), ct);
        return [];
    }

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceExplanation>>([]);

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceDependency>>([]);

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiffEntry>>([]);
}

internal sealed class FakeConfig
{
    public string Region { get; set; } = "eu";

    public int Replicas { get; set; }
}
