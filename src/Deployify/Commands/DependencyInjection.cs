using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Targets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deployify.Commands;

internal static class DependencyInjection
{
    /// <summary>Core services and every built-in target, logging to the console.</summary>
    public static IServiceCollection AddDeployifyCore(this IServiceCollection services)
        => services
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddDeployify();

    /// <summary>
    /// Resolves the project directory, active environment config, and target —
    /// then returns a fully wired session. See <see cref="DeploymentSessionResolver"/>.
    /// </summary>
    /// <param name="lockFor">
    /// Takes the environment's state lock for this operation (apply, destroy, …) before anything
    /// else is read, and holds it until the context is disposed. Null for read-only commands.
    /// </param>
    public static Task<ResolvedContext> ResolveAsync(
        string projectPath,
        string? envOverride,
        DeploymentTargetType? targetOverride,
        CancellationToken ct = default,
        string? lockFor = null)
    {
        IServiceProvider services = new ServiceCollection().AddDeployifyCore().BuildServiceProvider();
        return DeploymentSessionResolver.ResolveAsync(services, new DeploymentSessionRequest
        {
            ProjectPath = projectPath,
            Environment = envOverride,
            Target = targetOverride,
            LockFor = lockFor,
            Reporter = ConsoleDeploymentReporter.Instance
        }, ct);
    }
}
