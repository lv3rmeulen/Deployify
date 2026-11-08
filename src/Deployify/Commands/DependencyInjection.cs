using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Deployify.Target.AwsEcs;
using Deployify.Target.AzureContainerApps;
using Deployify.Target.Compose;
using Deployify.Target.Kubernetes;
using Deployify.Target.Nomad;
using Deployify.Target.NomadConsul;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Deployify.Commands;

internal static class DependencyInjection
{
    /// <summary>Core services — no target-specific clients.</summary>
    public static IServiceCollection AddDeployifyCore(this IServiceCollection services)
        => services
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddSingleton<SecretRegistry>()
            .AddSingleton<IStateManager>(sp => new StateManager(sp.GetRequiredService<SecretRegistry>()))
            .AddSingleton<IManifestReader, ManifestReader>()
            .AddSingleton<JobGenerator>()
            .AddSingleton<IPublisher, Publisher>()
            .AddSingleton<IDeploymentTarget, NomadDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, KubernetesDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, ComposeDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, EcsDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, AcaDeploymentTarget>()
            .AddSingleton<IDeploymentTarget, NomadConsulDeploymentTarget>()
            .AddSingleton<IDeploymentTargetFactory>(sp =>
                new DeploymentTargetFactory(sp.GetServices<IDeploymentTarget>()));

    /// <summary>
    /// Resolves the project directory, active environment config, and target —
    /// then returns a fully wired service provider.
    /// </summary>
    public static async Task<ResolvedContext> ResolveAsync(
        string projectPath,
        string? envOverride,
        DeploymentTargetType? targetOverride,
        CancellationToken ct = default)
    {
        var appHostDirectory = File.Exists(projectPath)
            ? Path.GetDirectoryName(Path.GetFullPath(projectPath))!
            : Path.GetFullPath(projectPath);

        // Walk up from appHostDirectory to find deployify-state.json
        var stateDirectory = FindStateDirectory(appHostDirectory)
            ?? throw new FileNotFoundException(
                $"deployify-state.json not found in '{appHostDirectory}' or any parent directory. " +
                "Run 'deployify init' first.");

        var sp0 = new ServiceCollection().AddDeployifyCore().BuildServiceProvider();
        var stateManager = sp0.GetRequiredService<IStateManager>();
        var (config, envName) = await stateManager.LoadActiveAsync(stateDirectory, envOverride, ct);

        var targetType = targetOverride ?? config.Target;
        var factory = sp0.GetRequiredService<IDeploymentTargetFactory>();
        var target = factory.Get(targetType);

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

            var logger = sp0.GetRequiredService<ILoggerFactory>().CreateLogger("Deployify.Secrets");
            generatedSecrets = await GeneratedSecretSession.OpenAsync(
                provider.CreateSecretStore(config, logger),
                new System.Collections.Generic.Dictionary<string, string>(parameterStore.Values),
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
        config.ParameterSource = new RecordingParameterSource(parameters, sp0.GetRequiredService<SecretRegistry>());

        // Prefer AppHostPath from state; fall back to the path the user passed.
        // Manifest resource paths are relative to the AppHost project directory.
        var resolvedAppHostDir = config.AppHostPath is not null && Directory.Exists(config.AppHostPath)
            ? config.AppHostPath
            : appHostDirectory;

        return new ResolvedContext(sp0, config, envName, targetType, target, stateDirectory)
        {
            AppHostDirectory = resolvedAppHostDir,
            GeneratedSecrets = generatedSecrets
        };
    }

    private static string? FindStateDirectory(string startDirectory)
    {
        var candidate = startDirectory;
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate, "deployify-state.json")))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }
}

/// <summary>Everything a command handler needs, resolved in one call.</summary>
internal sealed record ResolvedContext(
    IServiceProvider Services,
    EnvironmentConfig Config,
    string EnvName,
    DeploymentTargetType TargetType,
    IDeploymentTarget Target,
    string Directory)
{
    /// <summary>
    /// The Aspire AppHost project directory — used to resolve manifest resource paths.
    /// Differs from <see cref="Directory"/> when the state file is in a parent folder
    /// (e.g. repo root) and --project-path points to the AppHost subdirectory.
    /// </summary>
    public string AppHostDirectory { get; init; } = Directory;

    /// <summary>
    /// Generated parameter values held in an external secret backend, or null when the
    /// environment uses the inline backend (values kept in deployify-parameters.json).
    /// </summary>
    public GeneratedSecretSession? GeneratedSecrets { get; init; }

    /// <summary>
    /// Writes newly generated secret values to the backend. Apply calls this before deploying,
    /// so a service is never started with a password that isn't stored anywhere.
    /// </summary>
    public async Task SaveGeneratedSecretsAsync(CancellationToken ct = default)
    {
        if (GeneratedSecrets is { Pending.Count: > 0 } session)
        {
            var count = session.Pending.Count;
            await session.FlushAsync(ct);
            AnsiConsole.MarkupLine($"[dim]Stored {count} generated secret(s) in {Markup.Escape(session.Store.Description)}.[/]");
        }
    }

    /// <summary>Records where apply wrote each secret (locations and versions only) in the state file.</summary>
    public async Task SaveSecretReferencesAsync(CancellationToken ct = default)
    {
        if (!Config.Secrets.IsExternal || Config.Secrets.References.Count == 0)
        {
            return;
        }

        var stateManager = Services.GetRequiredService<IStateManager>();
        var file = await stateManager.LoadFileAsync(Directory, ct);
        if (file.Environments.TryGetValue(EnvName, out var env))
        {
            env.Secrets.References = Config.Secrets.References;
            await stateManager.SaveFileAsync(file, Directory, ct);
        }
    }

    /// <summary>
    /// Human-readable address/location for the active target, e.g. the Nomad address,
    /// Kubernetes context, AWS region, or ACA location. Used in status lines.
    /// </summary>
    public string DisplayAddress
    {
        get
        {
            var key = Config.Target switch
            {
                DeploymentTargetType.Nomad => "nomad",
                DeploymentTargetType.Kubernetes => "kubernetes",
                DeploymentTargetType.Compose => "compose",
                DeploymentTargetType.AwsEcs => "ecs",
                DeploymentTargetType.AzureContainerApps => "aca",
                DeploymentTargetType.NomadConsul => "nomadconsul",
                _ => Config.Target.ToString().ToLower()
            };
            if (!Config.TargetConfig.TryGetValue(key, out var raw) ||
                raw is not System.Text.Json.JsonElement el)
            {
                return "-";
            }

            return Config.Target switch
            {
                DeploymentTargetType.Nomad =>
                    el.TryGetProperty("address", out var a) ? a.GetString() ?? "-" : "-",
                DeploymentTargetType.Kubernetes =>
                    el.TryGetProperty("context", out var c) ? c.GetString() ?? "(current)" : "(current)",
                DeploymentTargetType.Compose =>
                    el.TryGetProperty("projectName", out var p) ? p.GetString() ?? "-" : "-",
                DeploymentTargetType.AwsEcs =>
                    el.TryGetProperty("region", out var r) ? r.GetString() ?? "-" : "-",
                DeploymentTargetType.AzureContainerApps =>
                    el.TryGetProperty("location", out var l) ? l.GetString() ?? "-" : "-",
                DeploymentTargetType.NomadConsul =>
                    el.TryGetProperty("nomadAddress", out var na) ? na.GetString() ?? "-" : "-",
                _ => "-"
            };
        }
    }

    /// <summary>
    /// Loads manifest.json from the output directory. If it doesn't exist, attempts to
    /// generate it from the AppHost project. Returns null and prints an error on failure.
    /// </summary>
    public async Task<AspireManifest?> LoadManifestAsync(CancellationToken ct = default)
    {
        var reader = Services.GetRequiredService<IManifestReader>();
        var path = Path.Combine(Directory, Config.OutputPath, "manifest.json");
        try
        {
            if (!File.Exists(path) || IsManifestStale(path, AppHostDirectory))
            {
                path = await reader.GenerateAsync(AppHostDirectory, Path.Combine(Directory, Config.OutputPath), ct);
            }

            var manifest = await reader.ReadAsync(path, ct);
            return manifest
                .WithDashboardIfEnabled(Config)
                .WithDaprIfEnabled(Config)
                .WithInferredReferences();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Could not load manifest: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Tip: run [cyan]deployify generate[/] first, or point --project-path at your AppHost .csproj.[/]");
            return null;
        }
    }

    /// <summary>
    /// True when the AppHost has changed since manifest.json was written, so the manifest
    /// has to be regenerated. Only applies when the AppHost directory actually contains a
    /// project file; a hand-placed manifest without an AppHost is always used as-is.
    /// </summary>
    private static bool IsManifestStale(string manifestPath, string appHostDirectory)
    {
        if (!System.IO.Directory.Exists(appHostDirectory) ||
            System.IO.Directory.GetFiles(appHostDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Length == 0)
        {
            return false;
        }

        var manifestTime = File.GetLastWriteTimeUtc(manifestPath);
        var sources = System.IO.Directory.EnumerateFiles(appHostDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
        return sources.Any(f => File.GetLastWriteTimeUtc(f) > manifestTime);
    }
}
