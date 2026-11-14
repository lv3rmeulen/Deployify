using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Deployify.Core.State;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Deployments;

/// <summary>What to resolve a <see cref="DeploymentSession"/> for.</summary>
public sealed class DeploymentSessionRequest
{
    /// <summary>The AppHost .csproj or its directory. deployify-state.json is searched from here upwards.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>The environment to use; null for the state file's active environment.</summary>
    public string? Environment { get; init; }

    /// <summary>Overrides the environment's configured target.</summary>
    public DeploymentTargetType? Target { get; init; }

    /// <summary>
    /// Takes the environment's state lock for this operation (apply, destroy, …) before anything
    /// else is read, and holds it until the session is disposed. Null for read-only runs.
    /// </summary>
    public string? LockFor { get; init; }

    /// <summary>See <see cref="DeploymentSession.ManifestPath"/>.</summary>
    public string? ManifestPath { get; init; }

    public IDeploymentReporter Reporter { get; init; } = NullDeploymentReporter.Instance;
}

public static class DeploymentSessionResolver
{
    public const string StateFileName = "deployify-state.json";

    /// <summary>
    /// Resolves the project directory, active environment config, and target, takes the lock
    /// when asked, and wires up parameter sources. <paramref name="services"/> must provide
    /// <see cref="IStateManager"/>, <see cref="IDeploymentTargetFactory"/>, <see cref="IManifestReader"/>,
    /// <see cref="SecretRegistry"/> and <see cref="ILoggerFactory"/>.
    /// </summary>
    public static async Task<DeploymentSession> ResolveAsync(
        IServiceProvider services, DeploymentSessionRequest request, CancellationToken ct = default)
    {
        var reporter = request.Reporter;
        var appHostDirectory = File.Exists(request.ProjectPath)
            ? Path.GetDirectoryName(Path.GetFullPath(request.ProjectPath))!
            : Path.GetFullPath(request.ProjectPath);

        // Walk up from appHostDirectory to find deployify-state.json
        var stateDirectory = FindStateDirectory(appHostDirectory)
            ?? throw new FileNotFoundException(
                $"{StateFileName} not found in '{appHostDirectory}' or any parent directory. " +
                "Run 'deployify init' first.");

        var stateManager = Get<IStateManager>(services);
        var (config, envName) = await stateManager.LoadActiveAsync(stateDirectory, request.Environment, ct);

        var targetType = request.Target ?? config.Target;
        var target = Get<IDeploymentTargetFactory>(services).Get(targetType, stateDirectory);
        if (target is IStartableTarget startable)
        {
            await startable.StartAsync(ct);
        }

        // Deployment state: lock first, then copy remote state in, so parameter values below
        // are the ones the last run (on any machine) stored.
        var state = new StateSession(stateDirectory, envName, config.State, onWarning: reporter.Warn);
        try
        {
            if (request.LockFor is not null)
            {
                var lease = await state.LockAsync(request.LockFor, ct);
                reporter.Info($"Locked {envName} in {state.Backend.Description} (lock id {lease.Info.Id}).");
            }

            try
            {
                await state.PullAsync(ct);
            }
            catch (Exception ex) when (request.LockFor is null && ex is not OperationCanceledException)
            {
                // Read-only runs still work from the local copy.
                reporter.Warn($"Could not read remote state ({ex.Message}); using the local copy.");
            }

            // Digests of published images, so artifacts pin exactly what was pushed.
            try
            {
                await PublishedImageStore.LoadIntoAsync(state, config.PublishedImages, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reporter.Warn($"Could not read published image digests ({ex.Message}); images are referenced by tag.");
            }
        }
        catch
        {
            await state.DisposeAsync();
            throw;
        }

        try
        {
            return await ResolveRestAsync(services, request, config, envName, targetType, target,
                stateDirectory, appHostDirectory, state, ct);
        }
        catch
        {
            await state.DisposeAsync();
            throw;
        }
    }

    private static async Task<DeploymentSession> ResolveRestAsync(
        IServiceProvider services, DeploymentSessionRequest request, EnvironmentConfig config, string envName,
        DeploymentTargetType targetType, IDeploymentTarget target, string stateDirectory, string appHostDirectory,
        StateSession state, CancellationToken ct)
    {
        // Parameter values: explicit config, Parameters__* env vars, manifest defaults, then
        // generated values. Those are kept in deployify-parameters.json, or with an external
        // secret backend, in the backend itself so no generated password is written to disk.
        var parameterStore = ParameterStore.Load(stateDirectory, envName);
        GeneratedSecretSession? generatedSecrets = null;
        IParameterSource parameters;
        if (config.Secrets.IsExternal)
        {
            if (target is not ISecretStoreProvider provider ||
                !provider.SupportedSecretBackends.Contains(config.Secrets.Backend))
            {
                var supported = target is ISecretStoreProvider p
                    ? string.Join(", ", p.SupportedSecretBackends.Prepend(SecretBackendType.Inline))
                    : nameof(SecretBackendType.Inline);
                throw new NotSupportedException(
                    $"The {target.DisplayName} target doesn't support the {config.Secrets.Backend} secret backend " +
                    $"(set in secrets.backend). Supported: {supported}.");
            }

            var logger = Get<ILoggerFactory>(services).CreateLogger("Deployify.Secrets");
            generatedSecrets = await GeneratedSecretSession.OpenAsync(
                provider.CreateSecretStore(config, logger),
                new Dictionary<string, string>(parameterStore.Values),
                logger, ct);
            parameters = ParameterSources.Default(
                ParameterResolution.ExplicitValues(config), generatedSecrets.Values,
                onGenerated: generatedSecrets.OnGenerated);
        }
        else
        {
            parameters = parameterStore.CreateSource(config);
        }

        // Every secret value resolved in this run is remembered, so the state file and
        // artifacts can be checked for it before they're written.
        config.ParameterSource = new RecordingParameterSource(parameters, Get<SecretRegistry>(services));

        // Prefer AppHostPath from state; fall back to the path that was passed in.
        // Manifest resource paths are relative to the AppHost project directory.
        var resolvedAppHostDir = config.AppHostPath is not null && Directory.Exists(config.AppHostPath)
            ? config.AppHostPath
            : appHostDirectory;

        return new DeploymentSession(services, config, envName, targetType, target, stateDirectory)
        {
            AppHostDirectory = resolvedAppHostDir,
            GeneratedSecrets = generatedSecrets,
            State = state,
            Reporter = request.Reporter,
            ManifestPath = request.ManifestPath
        };
    }

    /// <summary>The nearest folder at or above <paramref name="startDirectory"/> holding deployify-state.json.</summary>
    public static string? FindStateDirectory(string startDirectory)
    {
        var candidate = startDirectory;
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate, StateFileName)))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    private static T Get<T>(IServiceProvider services) where T : class
        => services.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException($"No service of type {typeof(T).Name} is registered.");
}
