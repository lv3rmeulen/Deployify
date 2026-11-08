using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.Interfaces;

/// <summary>
/// Optional capability of an <see cref="IDeploymentTarget"/>: find what Deployify deployed and
/// take it away again. <c>deployify destroy</c> and <c>apply --prune</c> use it; the decision of
/// what may be deleted is theirs (see <c>RemovalPlanner</c>), the target only talks to its platform.
/// </summary>
public interface IResourceLifecycle
{
    /// <summary>
    /// Services in this environment's scope (cluster, namespace, project, resource group) that
    /// carry Deployify's ownership marker, whatever state they are in (a stopped job counts).
    /// Must throw when the platform can't be asked: an empty list would read as "nothing there".
    /// </summary>
    Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default);

    /// <summary>
    /// Removes one service and what Deployify created for it alone (its platform-side secrets,
    /// unless <see cref="DeleteOptions.KeepSecrets"/>). Safe to repeat: a service that is
    /// already gone is not an error.
    /// </summary>
    Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default);

    /// <summary>
    /// Runs after destroy removed every service: environment-wide leftovers Deployify created
    /// (a Compose network, volumes when asked). Returns a line per thing it removed.
    /// </summary>
    Task<IReadOnlyList<string>> DestroyEnvironmentAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>What destroy deliberately leaves in place, shown in the destroy plan.</summary>
    IReadOnlyList<string> DescribeKept(EnvironmentConfig config, DeleteOptions options) => [];
}

/// <summary>
/// A service the platform marks as Deployify's. <see cref="Environment"/> is the environment
/// it was tagged with, on targets that tag it (null when the platform has no such tag).
/// </summary>
public sealed record ManagedService(string Name, string? TargetId = null, string? Environment = null);

public sealed class DeleteOptions
{
    /// <summary>Leave the service's platform-side secrets (Nomad variables, SSM parameters) in place.</summary>
    public bool KeepSecrets { get; init; }

    /// <summary>Also remove data volumes (Compose). Off by default: volumes hold data.</summary>
    public bool RemoveVolumes { get; init; }
}
