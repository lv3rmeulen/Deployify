using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.State;

namespace Deployify.Core.Deployments;

/// <summary>
/// Everything one run against an environment needs, resolved in one call by
/// <see cref="DeploymentSessionResolver"/>: the environment config, the target, the deployment
/// state (holding its lock when the run asked for one) and where parameter values come from.
/// Shared by the CLI commands and the Aspire <c>aspire deploy</c> integration.
/// </summary>
public sealed record DeploymentSession(
    IServiceProvider Services,
    EnvironmentConfig Config,
    string EnvName,
    DeploymentTargetType TargetType,
    IDeploymentTarget Target,
    string Directory) : IAsyncDisposable
{
    /// <summary>The environment's deployment state; holds the lock when the run asked for one.</summary>
    public required StateSession State { get; init; }

    /// <summary>
    /// Releases the state lock, and stops the target's process when it runs in one (a plugin
    /// target starts again on its next use).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await State.DisposeAsync();
        }
        finally
        {
            if (Target is IStartableTarget and IAsyncDisposable process)
            {
                await process.DisposeAsync();
            }
        }
    }

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

    /// <summary>Where this run reports progress, warnings and errors.</summary>
    public IDeploymentReporter Reporter { get; init; } = NullDeploymentReporter.Instance;

    /// <summary>
    /// A manifest someone else already wrote (the Aspire integration uses the one
    /// <c>aspire deploy</c> publishes). When set, <see cref="LoadManifestAsync"/> reads it as-is
    /// instead of generating one from the AppHost. Null for the CLI.
    /// </summary>
    public string? ManifestPath { get; init; }

    /// <summary>
    /// What the last <see cref="LoadManifestAsync"/> found about resources that won't be deployed.
    /// Its findings are already reported; commands that deploy check
    /// <see cref="ManifestAuditResult.Blocks"/> before changing anything.
    /// </summary>
    public ManifestAuditResult ManifestAudit { get; internal set; } = ManifestAuditResult.Empty;

    /// <summary>
    /// Adds the manifest resources that won't be deployed to <paramref name="plan"/>, so plan
    /// output lists them next to the changes.
    /// </summary>
    public PlanResult WithSkipped(PlanResult plan)
    {
        plan.Skipped = [.. ManifestAudit.Skipped];
        return plan;
    }

    /// <summary>Folder where generated artifacts are written.</summary>
    public string OutputDirectory => Path.Combine(Directory, Config.OutputPath);

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
            Reporter.Info($"Stored {count} generated secret(s) in {session.Store.Description}.");
        }

        // Inline backend: generated values are in deployify-parameters.json; share them through remote state.
        await State.PushParametersAsync(ct);
    }

    /// <summary>Records where apply wrote each secret (locations and versions only) in the state file.</summary>
    public async Task SaveSecretReferencesAsync(CancellationToken ct = default)
    {
        if (!Config.Secrets.IsExternal || Config.Secrets.References.Count == 0)
        {
            return;
        }

        var stateManager = GetRequiredService<IStateManager>();
        var file = await stateManager.LoadFileAsync(Directory, ct);
        if (file.Environments.TryGetValue(EnvName, out var env))
        {
            env.Secrets.References = Config.Secrets.References;
            await stateManager.SaveFileAsync(file, Directory, ct);
        }
    }

    /// <summary>
    /// After a publish: stores the image digests it recorded in deployment state, then
    /// regenerates the artifacts so they reference the images by those digests. Several targets
    /// apply the files on disk, which were written before the images existed. Failing to store
    /// the digests is only a warning: this run's artifacts still pin them, and later runs fall
    /// back to the tag.
    /// </summary>
    public async Task RecordPublishedImagesAsync(AspireManifest manifest, CancellationToken ct = default)
    {
        if (!Config.PublishedImages.HasChanges)
        {
            return;
        }

        try
        {
            await PublishedImageStore.SaveAsync(State, Config.PublishedImages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Reporter.Warn($"Could not record published image digests in state ({State.Backend.Description}): {ex.Message}");
        }

        await Target.GenerateAsync(manifest, Config, OutputDirectory, ct);
    }

    /// <summary>
    /// Human-readable address/location for the active target, e.g. the Nomad address,
    /// Kubernetes context, AWS region, or ACA location. Used in status lines.
    /// </summary>
    public string DisplayAddress
    {
        get
        {
            var key = Config.Target.Value switch
            {
                DeploymentTargetType.Names.Nomad => "nomad",
                DeploymentTargetType.Names.Kubernetes or DeploymentTargetType.Names.Helm => "kubernetes",
                DeploymentTargetType.Names.Compose => "compose",
                DeploymentTargetType.Names.AwsEcs => "ecs",
                DeploymentTargetType.Names.AzureContainerApps => "aca",
                DeploymentTargetType.Names.NomadConsul => "nomadconsul",
                DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux => "gitops",
                DeploymentTargetType.Names.OvhCloud => "ovhcloud",
                DeploymentTargetType.Names.Iis => "iis",
                _ => Config.Target.ToString().ToLower()
            };
            if (!Config.TargetConfig.TryGetValue(key, out var raw) ||
                raw is not System.Text.Json.JsonElement el)
            {
                return "-";
            }

            return Config.Target.Value switch
            {
                DeploymentTargetType.Names.Nomad =>
                    el.TryGetProperty("address", out var a) ? a.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.Kubernetes or DeploymentTargetType.Names.Helm =>
                    el.TryGetProperty("context", out var c) ? c.GetString() ?? "(current)" : "(current)",
                DeploymentTargetType.Names.Compose =>
                    el.TryGetProperty("projectName", out var p) ? p.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.AwsEcs =>
                    el.TryGetProperty("region", out var r) ? r.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.AzureContainerApps =>
                    el.TryGetProperty("location", out var l) ? l.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.NomadConsul =>
                    el.TryGetProperty("nomadAddress", out var na) ? na.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.ArgoCd or DeploymentTargetType.Names.Flux =>
                    el.TryGetProperty("repoUrl", out var g) ? g.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.OvhCloud =>
                    el.TryGetProperty("clusterId", out var k) || el.TryGetProperty("ClusterId", out k) ? k.GetString() ?? "-" : "-",
                DeploymentTargetType.Names.Iis =>
                    el.TryGetProperty("host", out var h) || el.TryGetProperty("Host", out h) ? h.GetString() ?? "localhost" : "localhost",
                _ => "-"
            };
        }
    }

    /// <summary>
    /// Loads the manifest. With <see cref="ManifestPath"/> set, reads that file. Otherwise loads
    /// manifest.json from the output directory, generating it from the AppHost project when it
    /// is missing or stale. Returns null and reports an error on failure.
    /// </summary>
    public async Task<AspireManifest?> LoadManifestAsync(CancellationToken ct = default)
    {
        var reader = GetRequiredService<IManifestReader>();
        try
        {
            AspireManifest manifest;
            if (ManifestPath is not null)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(ManifestPath))!;
                manifest = (await reader.ReadAsync(ManifestPath, ct))
                    .WithProjectPathsRelativeTo(directory)
                    .WithBuildPathsRelativeTo(directory);
            }
            else
            {
                var path = Path.Combine(OutputDirectory, "manifest.json");
                if (!File.Exists(path) || IsManifestStale(path, AppHostDirectory))
                {
                    path = await reader.GenerateAsync(AppHostDirectory, OutputDirectory, ct);
                }

                manifest = (await reader.ReadAsync(path, ct))
                    .WithBuildPathsRelativeTo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            }

            manifest = manifest
                .WithDashboardIfEnabled(Config)
                .WithDaprIfEnabled(Config)
                .WithInferredReferences();

            ManifestAudit = Deployments.ManifestAudit.Run(manifest, Config.IgnoreResources, Target.AdditionalResourceTypes);
            Deployments.ManifestAudit.Report(ManifestAudit, Reporter);
            return manifest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Reporter.Error(ManifestPath is null
                ? $"Could not load manifest: {ex.Message} Run 'deployify generate' first, or point --project-path at your AppHost .csproj."
                : $"Could not load manifest '{ManifestPath}': {ex.Message}");
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

    private T GetRequiredService<T>() where T : class
        => Services.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException($"No service of type {typeof(T).Name} is registered.");
}
