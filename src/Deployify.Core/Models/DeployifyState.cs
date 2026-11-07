using System.Collections.Generic;
using System.Text.Json.Serialization;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;

namespace Deployify.Core.Models;

/// <summary>
/// Root state file (deployify-state.json).
/// Holds multiple named environments and tracks which one is active.
/// </summary>
public sealed class DeployifyStateFile
{
    /// <summary>Name of the currently active environment.</summary>
    public string ActiveEnvironment { get; set; } = "local";

    /// <summary>All configured environments, keyed by name.</summary>
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = [];
}

/// <summary>
/// Per-environment configuration.
/// <see cref="Target"/> determines which deployment platform is used;
/// target-specific settings live in <see cref="TargetConfig"/>.
/// </summary>
public sealed class EnvironmentConfig
{
    /// <summary>
    /// The deployment platform for this environment.
    /// Drives which <see cref="IDeploymentTarget"/> is resolved at runtime.
    /// </summary>
    public DeploymentTargetType Target { get; set; } = DeploymentTargetType.Nomad;

    /// <summary>Container registry base URL, e.g. registry.example.com/myapp</summary>
    public string? ContainerRegistry { get; set; }

    /// <summary>Default image tag applied to every service</summary>
    public string ImageTag { get; set; } = "latest";

    /// <summary>Folder where generated output files are written (relative to AppHost dir)</summary>
    public string OutputPath { get; set; } = "deployify-output";

    /// <summary>Per-service overrides keyed by service name</summary>
    public Dictionary<string, ServiceOverride> ServiceOverrides { get; set; } = [];

    /// <summary>
    /// Values for Aspire <c>parameter.v0</c> resources, keyed by parameter name, or
    /// <c>name.input</c> for an input other than <c>value</c>. A value of <c>"env:NAME"</c> is read
    /// from environment variable NAME, which is how secret parameters should be supplied.
    /// Parameters not listed here fall back to a <c>Parameters__name</c> environment variable,
    /// then the manifest's default, then a generated value kept in deployify-parameters.json.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>
    /// Where parameter values come from at run time. Not persisted: the CLI sets it to a source
    /// backed by deployify-parameters.json, and <see cref="ParameterResolution.For"/> falls back
    /// to an in-memory source so every generator in one run sees the same values.
    /// </summary>
    [JsonIgnore]
    public IParameterSource? ParameterSource { get; set; }

    /// <summary>
    /// Container runtime used when publishing images via <c>dotnet publish -t:PublishContainer</c>.
    /// Defaults to <see cref="ContainerRuntime.Docker"/>.
    /// Set to <see cref="ContainerRuntime.Podman"/> to use rootless Podman instead.
    /// </summary>
    public ContainerRuntime ContainerRuntime { get; set; } = ContainerRuntime.Docker;

    /// <summary>
    /// When true, a deployment artifact for the Aspire Dashboard container is generated
    /// alongside the application services. Defaults to false.
    /// </summary>
    public bool DeployDashboard { get; set; } = false;

    /// <summary>
    /// Settings for the injected Aspire Dashboard container.
    /// Only used when <see cref="DeployDashboard"/> is true.
    /// </summary>
    public DashboardConfig Dashboard { get; set; } = new();

    /// <summary>
    /// Absolute path to the Aspire AppHost project directory.
    /// Used to resolve relative resource paths in the manifest (e.g. ../../External/...).
    /// Set automatically by <c>deployify init --project-path</c>.
    /// </summary>
    public string? AppHostPath { get; set; }

    /// <summary>
    /// Target OS for container images, passed as <c>--os</c> to dotnet publish.
    /// Defaults to "linux". Change to "windows" for Windows containers.
    /// </summary>
    public string ContainerOs { get; set; } = "linux";

    /// <summary>
    /// Target CPU architecture for container images, passed as <c>--arch</c> to dotnet publish.
    /// Defaults to "x64". Set to "arm64" when deploying to ARM64 hosts (e.g. Raspberry Pi, Apple Silicon).
    /// Common values: x64, arm64, arm.
    /// </summary>
    public string ContainerArch { get; set; } = "x64";

    /// Services that already have dapr annotations in the manifest are always handled;
    /// this flag additionally enables Dapr for services that don't have explicit annotations.
    /// </summary>
    public bool DeployDapr { get; set; } = false;

    /// <summary>
    /// Dapr sidecar configuration. Only used when <see cref="DeployDapr"/> is true
    /// or when dapr annotations are present on a resource.
    /// </summary>
    public DaprConfig Dapr { get; set; } = new();

    /// <summary>
    /// Target-specific configuration bag.
    /// Keyed by target name (e.g. "nomad", "kubernetes", "compose").
    /// Deserialized into the appropriate config type by the target itself.
    /// </summary>
    public Dictionary<string, object?> TargetConfig { get; set; } = [];
}

/// <summary>
/// Configuration for the optional Aspire Dashboard container deployment.
/// </summary>
public sealed class DashboardConfig
{
    /// <summary>
    /// Image tag for mcr.microsoft.com/dotnet/aspire-dashboard.
    /// Defaults to "latest". Pin to a specific version in production.
    /// </summary>
    public string ImageTag { get; set; } = "latest";

    /// <summary>Host port the dashboard UI is exposed on. Defaults to 18888.</summary>
    public int Port { get; set; } = 18888;

    /// <summary>Host port for the OTLP gRPC endpoint. Defaults to 18889.</summary>
    public int OtlpPort { get; set; } = 18889;

    /// <summary>
    /// When set, the dashboard login requires this token.
    /// Maps to DASHBOARD__FRONTEND__AUTHMODE=BrowserToken and
    /// DASHBOARD__FRONTEND__BROWSERTOKEN env vars.
    /// Leave null to run unauthenticated (suitable for private networks).
    /// </summary>
    public string? BrowserToken { get; set; }

    /// <summary>CPU reservation passed to the target (e.g. "100m" for Kubernetes, 100 for Nomad MHz).</summary>
    public string Cpu { get; set; } = "100m";

    /// <summary>Memory reservation passed to the target (e.g. "128Mi" for Kubernetes, 128 for Nomad MB).</summary>
    public string Memory { get; set; } = "128Mi";
}

/// <summary>
/// Configuration for Dapr sidecar injection.
/// </summary>
public sealed class DaprConfig
{
    /// <summary>
    /// Dapr sidecar image tag. Defaults to "latest".
    /// Used for Nomad (explicit sidecar task) and Compose (daprd container).
    /// Kubernetes and ACA use the platform-managed sidecar injector.
    /// </summary>
    public string DaprVersion { get; set; } = "latest";

    /// <summary>
    /// Base port for Dapr HTTP API on each service (e.g. 3500).
    /// For Nomad/Compose where ports must not collide, each service gets
    /// DaprHttpPort + index.
    /// </summary>
    public int DaprHttpPort { get; set; } = 3500;

    /// <summary>Base port for Dapr gRPC API (e.g. 50001).</summary>
    public int DaprGrpcPort { get; set; } = 50001;

    /// <summary>
    /// Path to Dapr components directory (for Nomad and Compose).
    /// Mounted into the sidecar container at runtime.
    /// </summary>
    public string? ComponentsPath { get; set; }

    /// <summary>
    /// Path to Dapr configuration file (for Nomad and Compose).
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Log level for the Dapr sidecar. Defaults to "info".
    /// </summary>
    public string LogLevel { get; set; } = "info";
}

/// <summary>
/// Constants for Dapr sidecar injection.
/// </summary>
public static class DaprConstants
{
    public const string SidecarImage = "daprio/daprd";
    public const string AppIdAnnotation = "dapr/app-id";
    public const string AppPortAnnotation = "dapr/app-port";
    public const string EnabledAnnotation = "dapr/enabled";
    public const string HttpPortAnnotation = "dapr/http-port";
    public const string GrpcPortAnnotation = "dapr/grpc-port";
}

/// <summary>
/// Constants for the injected Aspire Dashboard resource.
/// Targets use these to recognize and handle the dashboard specially if needed.
/// </summary>
public static class AspireDashboard
{
    public const string ResourceName = "aspire-dashboard";
    public const string Image = "mcr.microsoft.com/dotnet/aspire-dashboard";
    public const string ResourceType = "container.v0";
}

public sealed class ServiceOverride
{
    public string? ImageTag { get; set; }
    public int? Count { get; set; }
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>
    /// Artifact download URL written by <c>deployify package</c>.
    /// Used by the Nomad raw_exec job generator to emit the <c>artifact { source }</c> block.
    /// </summary>
    public string? ArtifactUrl { get; set; }
}

public enum DeploymentMode
{
    Docker,
    RawExec
}

public enum ContainerRuntime
{
    /// <summary>Use Docker (default). Passes no extra flags to dotnet publish.</summary>
    Docker,

    /// <summary>
    /// Use Podman. Passes <c>-p:LocalRegistry=podman</c> to dotnet publish,
    /// and uses <c>podman</c> for any direct container CLI calls (network, volume).
    /// Requires .NET 8+ SDK and Podman 4+ on PATH.
    /// </summary>
    Podman
}

/// <summary>
/// Shared JSON options for deserializing target config bags.
/// Must include <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// so that enum values stored as strings (e.g. "Docker", "Fargate") round-trip correctly.
/// </summary>
public static class TargetConfigJsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
