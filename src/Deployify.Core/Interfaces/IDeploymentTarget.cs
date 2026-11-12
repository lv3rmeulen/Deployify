using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

/// <summary>
/// Represents a deployment platform (Nomad, Kubernetes, Compose, …).
/// Each target handles its own plan/apply/validate/diff/publish/explain/graph cycle.
/// Register implementations via <see cref="IDeploymentTargetFactory"/>.
/// </summary>
public interface IDeploymentTarget
{
    /// <summary>The target type this implementation handles.</summary>
    DeploymentTargetType TargetType { get; }

    /// <summary>Human-readable name, e.g. "HashiCorp Nomad".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Manifest resource types this target deploys beyond Deployify's own workloads (projects and
    /// containers), e.g. <c>executable.v0</c> for a target that runs executables. Resources of
    /// these types aren't reported as skipped. Empty for the built-in targets.
    /// </summary>
    IReadOnlyCollection<string> AdditionalResourceTypes => [];

    /// <summary>
    /// Checks connectivity to the target cluster/daemon.
    /// Returns (true, null) on success, or (false, errorMessage) on failure.
    /// </summary>
    Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default);

    /// <summary>
    /// Generates all output artifacts (e.g. HCL files, Kubernetes manifests, compose.yml)
    /// from the Aspire manifest. Does not deploy anything.
    /// Returns the paths of all generated files.
    /// </summary>
    Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Validates generated artifacts against the target.
    /// Returns a list of (ArtifactName, errors) tuples.
    /// </summary>
    Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a <see cref="PlanResult"/> describing what would change
    /// if <see cref="ApplyAsync"/> were called.
    /// </summary>
    Task<PlanResult> PlanAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Deploys all generated artifacts to the target.
    /// Returns a list of (serviceName, deploymentId) tuples.
    /// </summary>
    Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Builds and pushes images / raw-exec archives for project-based services.
    /// </summary>
    Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Returns human-readable explanations for each service in the manifest.
    /// Async so a target can ask something else (a plugin process, an API) for the answer.
    /// </summary>
    Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// Returns dependency edges between services for graph rendering.
    /// </summary>
    Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        CancellationToken ct = default);

    /// <summary>
    /// Compares the live state of the target against the local artifacts.
    /// </summary>
    Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default);
}

// ── Result types returned by IDeploymentTarget methods ────────────────────────

public sealed class ValidationResult
{
    public string ArtifactName { get; init; } = string.Empty;
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed class DeploymentResult
{
    public string ServiceName { get; init; } = string.Empty;
    public string? DeploymentId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed class ServiceExplanation
{
    public string Name { get; init; } = string.Empty;
    public string? Image { get; init; }
    public string? ImageTag { get; init; }
    public int Count { get; init; } = 1;
    public Dictionary<string, string> Env { get; init; } = [];
    public List<string> Ports { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public Dictionary<string, string> AdditionalInfo { get; init; } = [];
}

public sealed class ServiceDependency
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}
