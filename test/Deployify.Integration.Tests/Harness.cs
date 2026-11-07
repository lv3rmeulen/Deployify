using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Services;
using NSubstitute;

namespace Deployify.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "deployify-int-" + System.IO.Path.GetRandomFileName());

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Builds a minimal deployify-state.json in <paramref name="dir"/> and returns the root command.
/// All target calls go to a NSubstitute mock; the manifest.json is written to the output dir.
/// </summary>
internal static class Harness
{
    // ── State file helpers ────────────────────────────────────────────────────

    public static async Task WriteStateAsync(string dir, DeployifyStateFile state, CancellationToken ct = default)
    {
        var sm = new StateManager();
        await sm.SaveFileAsync(state, dir, ct);
    }

    public static async Task WriteNomadStateAsync(string dir,
        string envName = "local",
        string address = "http://localhost:4646",
        string ns = "default",
        string dc = "dc1",
        string registry = "reg.example.com/app",
        string tag = "v1.0.0",
        bool deployDashboard = false,
        bool deployDapr = false,
        CancellationToken ct = default)
    {
        var cfg = NomadConfig(address, ns, dc, registry, tag);
        cfg.DeployDashboard = deployDashboard;
        cfg.DeployDapr = deployDapr;
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteMultiEnvStateAsync(string dir, CancellationToken ct = default)
    {
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = "local",
            Environments =
            {
                ["local"] = NomadConfig(),
                ["prod"] = NomadConfig("http://nomad.prod:4646", "production", "us-east-1")
            }
        }, ct);
    }

    public static EnvironmentConfig NomadConfig(
        string address = "http://localhost:4646",
        string ns = "default",
        string dc = "dc1",
        string registry = "reg.example.com/app",
        string tag = "v1.0.0")
    {
        var nomadCfg = new Target.Nomad.NomadTargetConfig
        {
            Address = address,
            Namespace = ns,
            Datacenters = [dc]
        };
        return new EnvironmentConfig
        {
            Target = DeploymentTargetType.Nomad,
            ContainerRegistry = registry,
            ImageTag = tag,
            OutputPath = "deployify-output",
            TargetConfig = { ["nomad"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(nomadCfg)) }
        };
    }

    public static async Task WriteKubernetesStateAsync(string dir,
        string envName = "local",
        string namespacePrefix = "test",
        string registry = "reg.example.com/app",
        string tag = "v1.0.0",
        CancellationToken ct = default)
    {
        var kubernetesCfg = new Target.Kubernetes.KubernetesTargetConfig
        {
            NamespacePrefix = namespacePrefix, DefaultReplicas = 1,
            CpuRequest = "100m", CpuLimit = "500m",
            MemoryRequest = "128Mi", MemoryLimit = "512Mi"
        };
        var cfg = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Kubernetes,
            ContainerRegistry = registry, ImageTag = tag, OutputPath = "deployify-output",
            TargetConfig = { ["kubernetes"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(kubernetesCfg))
            }
        };
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteComposeStateAsync(string dir,
        string envName = "local",
        string projectName = "testproj",
        string registry = "reg.example.com/app",
        string tag = "v1.0.0",
        bool deployDashboard = false,
        bool deployDapr = false,
        CancellationToken ct = default)
    {
        var composeCfg = new Target.Compose.ComposeTargetConfig
        {
            ProjectName = projectName, RestartPolicy = "unless-stopped",
            GenerateProfiles = false, GenerateEnvFile = false,
            GenerateMergedFile = true, GenerateHealthChecks = true
        };
        var cfg = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Compose,
            ContainerRegistry = registry, ImageTag = tag, OutputPath = "deployify-output",
            DeployDashboard = deployDashboard,
            DeployDapr = deployDapr,
            TargetConfig = { ["compose"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(composeCfg))
            }
        };
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteEcsStateAsync(string dir,
        string envName = "local",
        string region = "us-east-1",
        string cluster = "test-cluster",
        string registry = "123456789.dkr.ecr.us-east-1.amazonaws.com/myapp",
        string tag = "v1.0.0",
        CancellationToken ct = default)
    {
        var ecsCfg = new Target.AwsEcs.EcsTargetConfig
        {
            Region = region,
            ClusterName = cluster,
            DefaultCpu = 256,
            DefaultMemory = 512,
            DefaultDesiredCount = 1
        };
        var cfg = new EnvironmentConfig
        {
            Target = DeploymentTargetType.AwsEcs,
            ContainerRegistry = registry, ImageTag = tag, OutputPath = "deployify-output",
            TargetConfig = { ["ecs"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(ecsCfg))
            }
        };
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteAcaStateAsync(string dir,
        string envName = "local",
        string resourceGroup = "test-rg",
        string location = "eastus",
        string registry = "myregistry.azurecr.io/myapp",
        string tag = "v1.0.0",
        CancellationToken ct = default)
    {
        var acaCfg = new Target.AzureContainerApps.AcaTargetConfig
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup = resourceGroup,
            Location = location,
            EnvironmentName = "test-env",
            MinReplicas = 0,
            MaxReplicas = 3
        };
        var cfg = new EnvironmentConfig
        {
            Target = DeploymentTargetType.AzureContainerApps,
            ContainerRegistry = registry, ImageTag = tag, OutputPath = "deployify-output",
            TargetConfig = { ["aca"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(acaCfg))
            }
        };
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteNomadConsulStateAsync(string dir,
        string envName = "local",
        string nomadAddr = "http://localhost:4646",
        string consulAddr = "http://localhost:8500",
        string ns = "default",
        string dc = "dc1",
        string registry = "reg.example.com/app",
        string tag = "v1.0.0",
        bool deployDashboard = false,
        bool deployDapr = false,
        CancellationToken ct = default)
    {
        var ncCfg = new Target.NomadConsul.NomadConsulTargetConfig
        {
            NomadAddress = nomadAddr,
            NomadNamespace = ns,
            Datacenters = [dc],
            ConsulAddress = consulAddr,
            UpstreamBasePort = 20000,
            EnableIngressGateway = false
        };
        var cfg = new EnvironmentConfig
        {
            Target = DeploymentTargetType.NomadConsul,
            ContainerRegistry = registry, ImageTag = tag, OutputPath = "deployify-output",
            DeployDashboard = deployDashboard,
            DeployDapr = deployDapr,
            TargetConfig = { ["nomadconsul"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(ncCfg))
            }
        };
        await WriteStateAsync(dir, new DeployifyStateFile
        {
            ActiveEnvironment = envName,
            Environments = { [envName] = cfg }
        }, ct);
    }

    public static async Task WriteManifestAsync(string dir, string outputPath = "deployify-output", CancellationToken ct = default)
    {
        var outputDir = Path.Combine(dir, outputPath);
        Directory.CreateDirectory(outputDir);
        var manifest = new
        {
            resources = new
            {
                api = new { type = "project.v0", path = "Api/Api.csproj",
                    env = new { ASPNETCORE_ENVIRONMENT = "Production" },
                    bindings = new { http = new { containerPort = 8080, protocol = "http" } } },
                worker = new { type = "project.v0", path = "Worker/Worker.csproj",
                    references = new[] { new { name = "api" } } },
                redis = new { type = "container.v0", image = "redis:7",
                    bindings = new { tcp = new { containerPort = 6379, protocol = "tcp" } } }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest), ct);
    }

    // ── Mock target ───────────────────────────────────────────────────────────

    public static IDeploymentTarget MockNomadTarget(
        PlanResult? plan = null,
        IReadOnlyList<DeploymentResult>? applyResults = null,
        IReadOnlyList<ValidationResult>? validateResults = null,
        IReadOnlyList<DiffEntry>? diffEntries = null,
        bool pingOk = true)
    {
        var mock = Substitute.For<IDeploymentTarget>();
        mock.TargetType.Returns(DeploymentTargetType.Nomad);
        mock.DisplayName.Returns("HashiCorp Nomad");

        mock.PingAsync(Arg.Any<EnvironmentConfig>(), Arg.Any<CancellationToken>())
            .Returns(pingOk ? (true, null) : (false, "Connection refused"));

        mock.PlanAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(plan ?? new PlanResult
            {
                ToCreate = ["api", "worker", "redis"],
                ToUpdate = [],
                ToDelete = []
            });

        mock.ApplyAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(applyResults ??
            [
                new DeploymentResult { ServiceName = "api", Success = true, DeploymentId = "abc123" },
                new DeploymentResult { ServiceName = "worker", Success = true },
                new DeploymentResult { ServiceName = "redis", Success = true }
            ]);

        mock.ValidateAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(validateResults ??
            [
                new ValidationResult { ArtifactName = "api.job.hcl", Errors = [] },
                new ValidationResult { ArtifactName = "worker.job.hcl", Errors = [] },
                new ValidationResult { ArtifactName = "redis.job.hcl", Errors = [] }
            ]);

        mock.DiffAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(diffEntries ??
            [
                new DiffEntry { ServiceName = "api", Kind = DiffKind.Unchanged },
                new DiffEntry { ServiceName = "worker", Kind = DiffKind.Unchanged },
                new DiffEntry { ServiceName = "redis", Kind = DiffKind.Unchanged }
            ]);

        mock.GenerateAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        mock.Explain(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>())
            .Returns([
                new ServiceExplanation { Name = "api", Image = "reg.example.com/app/api", ImageTag = "v1.0.0", Count = 1, Ports = ["8080:http"] },
                new ServiceExplanation { Name = "worker", Image = "reg.example.com/app/worker", ImageTag = "v1.0.0", Count = 1 },
                new ServiceExplanation { Name = "redis", Image = "redis:7", ImageTag = "latest", Count = 1 }
            ]);

        mock.GetDependencies(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>())
            .Returns([
                new ServiceDependency { From = "worker", To = "api" }
            ]);

        mock.PublishAsync(Arg.Any<AspireManifest>(), Arg.Any<EnvironmentConfig>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return mock;
    }

    // ── Root command builder ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a RootCommand that is identical to production except
    /// <see cref="DependencyInjection.AddDeployifyCore"/> has its target replaced by the mock.
    /// Because the commands call DependencyInjection.ResolveAsync internally (which rebuilds DI),
    /// we use a test-only overload that accepts a mock factory via an environment variable trick
    /// — instead, we test the commands that do NOT call ResolveAsync (init, env) directly,
    /// and for the ones that do (plan, apply, etc.) we test them via a lightweight shim that
    /// substitutes the DI resolution step.
    /// </summary>
    public static RootCommand BuildRoot() => new("deployify-test")
    {
        InitCommand.Build(),
        EnvCommand.Build(),
    };
}
