using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands.Json;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Deployify.Plugins;
using Deployify.Targets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deployify.Commands;

internal static class DependencyInjection
{
    /// <summary>Core services and every built-in target, logging to the console.</summary>
    /// <param name="allowUnverifiedPlugins">Start plugins the lock file has no hash for, with a warning (<c>--allow-unverified</c>).</param>
    /// <remarks>
    /// With <c>--output json</c> the logs go to stderr, where the rest of the human-readable output
    /// goes, so stdout carries nothing but the JSON document. The same for a command that keeps
    /// stdout for its own output (<see cref="ConsoleRouting.StdoutReserved"/>, <c>deployify logs</c>).
    /// </remarks>
    public static IServiceCollection AddDeployifyCore(this IServiceCollection services, bool allowUnverifiedPlugins = false)
    {
        var logToStderr = JsonOutput.Current is not null || ConsoleRouting.StdoutReserved;
        return services
            .AddLogging(b => b
                .AddConsole(o =>
                {
                    if (logToStderr)
                    {
                        o.LogToStandardErrorThreshold = LogLevel.Trace;
                    }
                })
                .SetMinimumLevel(LogLevel.Information))
            .AddDeployify()
            .AddSingleton(new PluginHostOptions { AllowUnverified = allowUnverifiedPlugins });
    }

    /// <summary>
    /// Resolves the project directory, active environment config, and target —
    /// then returns a fully wired session. See <see cref="DeploymentSessionResolver"/>.
    /// </summary>
    /// <param name="lockFor">
    /// Takes the environment's state lock for this operation (apply, destroy, …) before anything
    /// else is read, and holds it until the context is disposed. Null for read-only commands.
    /// </param>
    /// <param name="allowUnverifiedPlugins">
    /// Start a plugin target that deployify-plugins.lock.json has no hash for, warning each time
    /// (<see cref="GlobalOptions.AllowUnverified"/>). Without it such a plugin is refused.
    /// </param>
    public static Task<ResolvedContext> ResolveAsync(
        string projectPath,
        string? envOverride,
        DeploymentTargetType? targetOverride,
        CancellationToken ct = default,
        string? lockFor = null,
        bool allowUnverifiedPlugins = false)
    {
        IServiceProvider services = new ServiceCollection().AddDeployifyCore(allowUnverifiedPlugins).BuildServiceProvider();
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
