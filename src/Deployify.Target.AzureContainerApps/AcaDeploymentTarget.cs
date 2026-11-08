using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.SecretBackends.KeyVault;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// IDeploymentTarget implementation for Azure Container Apps.
///
/// Output structure:
///   {outputDir}/
///     {service}.containerapp.json    ← Container App ARM/YAML spec
///
/// Apply strategy:
///   1. Write app specs to disk (PlanAsync)
///   2. Deploy via `az containerapp create/update --yaml` (ApplyAsync)
///
/// Requires the Azure CLI to be installed and logged in.
/// </summary>
public sealed class AcaDeploymentTarget(
    IPublisher publisher,
    ILogger<AcaDeploymentTarget> logger) : IDeploymentTarget, ISecretStoreProvider, IRolloutTarget, IResourceLifecycle
{
    private readonly AcaManifestGenerator _generator = new();

    // ── Secrets ───────────────────────────────────────────────────────────────

    public IReadOnlyCollection<SecretBackendType> SupportedSecretBackends { get; } = [SecretBackendType.KeyVault];

    public ISecretStore CreateSecretStore(EnvironmentConfig config, ILogger storeLogger)
    {
        var cfg = GetConfig(config);
        var keyVault = config.Secrets.KeyVault;
        return new KeyVaultSecretStore(
            new AzureCliKeyVaultClient(cfg.SubscriptionId), KeyVaultSecrets.VaultName(keyVault), AcaKeyVault.Prefix(keyVault, cfg));
    }

    /// <summary>
    /// Key Vault backend: writes an app's secret env vars to Key Vault before the app is
    /// deployed, and records where they went. Returns true when an existing secret changed,
    /// which a running revision only notices after a restart.
    /// </summary>
    internal static async Task<bool> SyncSecretsAsync(
        AcaApp app, EnvironmentConfig config, AcaTargetConfig cfg, IKeyVaultClient client, CancellationToken ct)
    {
        if (config.Secrets.Backend != SecretBackendType.KeyVault || app.SecretEnv.Count == 0)
        {
            return false;
        }

        var keyVault = config.Secrets.KeyVault;
        var vaultName = KeyVaultSecrets.VaultName(keyVault);
        var names = AcaManifestGenerator.SecretNames(app);
        var tags = new Dictionary<string, string>
        {
            [KeyVaultSecrets.StoreTag] = AcaKeyVault.Prefix(keyVault, cfg),
            [KeyVaultSecrets.AppTag] = app.Name
        };
        var changed = false;
        foreach (var (key, value) in app.SecretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var name = AcaKeyVault.AppSecretName(keyVault, cfg, app.Name, names[key]);
            var (version, written, existed) = await KeyVaultSecrets.SetIfChangedAsync(
                client, vaultName, name, new SensitiveValue(value.ToString()), tags, ct);
            changed |= written && existed;
            config.Secrets.References[$"{app.Name}/{key}"] = new SecretReference
            {
                Backend = SecretBackendType.KeyVault,
                Location = KeyVaultSecrets.SecretUrl(keyVault, name),
                Version = version
            };
        }

        return changed;
    }

    public DeploymentTargetType TargetType => DeploymentTargetType.AzureContainerApps;
    public string DisplayName => "Azure Container Apps";

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(
        EnvironmentConfig config, CancellationToken ct = default)
    {
        if (!await AcaCliClient.IsAvailableAsync(ct))
        {
            return (false, "Azure CLI ('az') not found on PATH. Install it from https://aka.ms/install-azure-cli");
        }

        var cfg = GetConfig(config);
        var client = Client(cfg);
        return await client.PingAsync(ct);
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var paths = new List<string>();
        foreach (var app in apps)
        {
            paths.Add(await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct));
        }

        return paths;
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var results = new List<ValidationResult>();
        var client = Client(cfg);

        foreach (var app in apps)
        {
            var path = await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct);
            var errors = await client.ValidateAppSpecAsync(path, ct);
            results.Add(new ValidationResult { ArtifactName = path, Errors = errors });
        }

        return results;
    }

    // ── Plan ─────────────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        // The app spec needs the subscription to build the environment ID.
        if (string.IsNullOrWhiteSpace(cfg.SubscriptionId))
        {
            cfg.SubscriptionId = await new AcaCliClient(cfg, logger).GetCurrentSubscriptionAsync(ct) ?? string.Empty;
        }

        var apps = _generator.Generate(manifest, config, cfg);

        // Write specs to disk — apply reads these
        foreach (var app in apps)
        {
            await _generator.WriteAppSpecAsync(app, config, cfg, outputDirectory, ct);
        }

        var desired = apps.Select(a => a.Name).ToHashSet();
        var client = Client(cfg);
        HashSet<string> running;
        try
        {
            running = [.. await client.GetRunningAppNamesAsync(ct)];
        }
        catch
        {
            running = [];
        }

        return new PlanResult
        {
            ToCreate = [.. desired.Except(running)],
            ToUpdate = [.. desired.Intersect(running)],
            ToDelete = [.. (await RemovableAsync(client, cfg, ct)).Except(desired)],
            Unchanged = []
        };
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var client = Client(cfg);
        var results = new List<DeploymentResult>();

        foreach (var app in apps)
        {
            results.Add(await ApplyOneAsync(app, config, cfg, client, outputDirectory, ct));
        }

        return results;
    }

    private async Task<DeploymentResult> ApplyOneAsync(
        AcaApp app, EnvironmentConfig config, AcaTargetConfig cfg, AcaCliClient client, string outputDirectory,
        CancellationToken ct)
    {
        var specPath = Path.Combine(outputDirectory, $"{app.Name}.containerapp.json");
        if (!File.Exists(specPath))
        {
            return new DeploymentResult
            {
                ServiceName = app.Name,
                Success = false,
                Error = $"App spec not found: {specPath}. Run 'deployify plan' first."
            };
        }

        try
        {
            // Secrets first: a new revision reads its Key Vault references when it starts.
            var secretsChanged = await SyncSecretsAsync(app, config, cfg, KeyVaultClientFactory(cfg.SubscriptionId), ct);
            var resourceId = await client.DeployAppAsync(specPath, app.Name, ct);
            logger.LogInformation("Deployed Container App '{Name}' → {Id}", app.Name, resourceId);

            // An unchanged spec makes no new revision, and Container Apps only re-reads
            // Key Vault references periodically, so restart to pick up new values now.
            if (secretsChanged && await client.GetLatestRevisionAsync(app.Name, ct) is { } revision &&
                !await client.RestartRevisionAsync(app.Name, revision, ct))
            {
                logger.LogWarning("Secrets of {Name} changed but revision {Revision} could not be restarted.", app.Name, revision);
            }

            return new DeploymentResult
            {
                ServiceName = app.Name,
                DeploymentId = resourceId,
                Success = true
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deploy Container App '{Name}'.", app.Name);
            return new DeploymentResult
            {
                ServiceName = app.Name,
                Success = false,
                Error = ex.Message
            };
        }
    }

    // ── Rollout (health-gated apply) ──────────────────────────────────────────

    /// <summary>Process runner for the az CLI; tests swap in a fake.</summary>
    internal Deployify.Core.Processes.IProcessRunner? Runner { get; init; }

    private AcaCliClient Client(AcaTargetConfig cfg) => new(cfg, logger, Runner);

    /// <summary>Creates Key Vault clients (by subscription); replaceable in tests.</summary>
    internal Func<string, IKeyVaultClient> KeyVaultClientFactory { get; init; } =
        subscription => new AzureCliKeyVaultClient(subscription);

    private const string KeyVaultVersionsKey = "keyVaultVersions";

    /// <summary>
    /// Records the revision that serves traffic now (the latest ready one) and, with the Key
    /// Vault backend, the version of each of the app's secrets: versions only, never values.
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var app = await Client(cfg).GetAppAsync(service, ct);
        var revision = app?.LatestReadyRevision ?? app?.LatestRevision;
        if (app is null || revision is null)
        {
            return new ServiceSnapshot { ServiceName = service, Existed = app is not null };
        }

        var data = new Dictionary<string, string>();
        if (await CaptureKeyVaultVersionsAsync(manifest, config, cfg, service, ct) is { } versions)
        {
            data[KeyVaultVersionsKey] = versions;
        }

        return new ServiceSnapshot { ServiceName = service, Existed = true, Revision = revision, Data = data };
    }

    private async Task<string?> CaptureKeyVaultVersionsAsync(
        AspireManifest manifest, EnvironmentConfig config, AcaTargetConfig cfg, string service, CancellationToken ct)
    {
        if (config.Secrets.Backend != SecretBackendType.KeyVault)
        {
            return null;
        }

        var app = _generator.Generate(manifest, config, cfg).FirstOrDefault(a => a.Name == service);
        if (app is null || app.SecretEnv.Count == 0)
        {
            return null;
        }

        var keyVault = config.Secrets.KeyVault;
        var vaultName = KeyVaultSecrets.VaultName(keyVault);
        var client = KeyVaultClientFactory(cfg.SubscriptionId);
        var names = AcaManifestGenerator.SecretNames(app);
        var versions = new Dictionary<string, string>();
        try
        {
            foreach (var key in app.SecretEnv.Keys)
            {
                var name = AcaKeyVault.AppSecretName(keyVault, cfg, app.Name, names[key]);
                if (await client.GetSecretAsync(vaultName, name, ct) is { } secret)
                {
                    versions[name] = secret.Version;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not read the Key Vault secrets of {Name} ({Message}); a rollback will not restore them.",
                service, ex.Message);
            return null;
        }

        return versions.Count == 0 ? null : JsonSerializer.Serialize(versions);
    }

    /// <summary>
    /// Writes each captured secret version back as the newest version, since apps reference
    /// secrets without a version. Returns true when anything was written.
    /// </summary>
    private async Task<bool> RestoreKeyVaultVersionsAsync(
        EnvironmentConfig config, AcaTargetConfig cfg, ServiceSnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.Data.TryGetValue(KeyVaultVersionsKey, out var raw))
        {
            return false;
        }

        var versions = JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new Dictionary<string, string>();
        var keyVault = config.Secrets.KeyVault;
        var vaultName = KeyVaultSecrets.VaultName(keyVault);
        var client = KeyVaultClientFactory(cfg.SubscriptionId);
        var tags = new Dictionary<string, string>
        {
            [KeyVaultSecrets.StoreTag] = AcaKeyVault.Prefix(keyVault, cfg),
            [KeyVaultSecrets.AppTag] = snapshot.ServiceName
        };

        var changed = false;
        foreach (var (name, version) in versions)
        {
            var current = await client.GetSecretAsync(vaultName, name, ct);
            if (current?.Version == version)
            {
                continue;
            }

            var old = await client.GetSecretVersionAsync(vaultName, name, version, ct)
                      ?? throw new InvalidOperationException($"Key Vault secret {name} no longer has version {version}.");
            await client.SetSecretAsync(vaultName, name, old.Value.Reveal(), tags, ct);
            logger.LogInformation("Restored Key Vault secret {Name} to version {Version}'s value.", name, version);
            changed = true;
        }

        return changed;
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var app = _generator.Generate(manifest, config, cfg).FirstOrDefault(a => a.Name == service);
        if (app is null)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = "not a Container App in this manifest" };
        }

        var client = Client(cfg);
        var result = await ApplyOneAsync(app, config, cfg, client, outputDirectory, ct);
        if (!result.Success)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = result.Error };
        }

        // `containerapp update` waits for the revision to be created, so the app names it now.
        var status = await client.GetAppAsync(service, ct);
        return status?.LatestRevision is { } latest
            ? new AppliedRevision { ServiceName = service, Success = true, Revision = latest }
            : new AppliedRevision { ServiceName = service, Success = false, Error = "Azure reported no revision after the deploy" };
    }

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var app = await client.GetAppAsync(service, ct);
        var rev = app is null ? null : await client.GetRevisionAsync(service, revision, ct);
        return AcaRolloutHealth.Evaluate(app, rev, revision);
    }

    /// <summary>
    /// Copies the captured revision into a new one, which Container Apps then rolls out; returns
    /// the new revision's name. An app this rollout created is deleted.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var client = Client(GetConfig(config));
        var service = snapshot.ServiceName;
        var app = await client.GetAppAsync(service, ct);

        if (snapshot is not { Existed: true, Revision: { } previous })
        {
            if (app is not null && !snapshot.Existed)
            {
                await client.DeleteAppAsync(service, ct);
                logger.LogInformation("Deleted new Container App {Name}.", service);
            }

            return null;
        }

        if (app is null)
        {
            throw new InvalidOperationException($"Container App '{service}' no longer exists, so it can't be rolled back.");
        }

        // Secrets first: the copied revision reads its Key Vault references when it starts.
        var cfg = GetConfig(config);
        var secretsRestored = await RestoreKeyVaultVersionsAsync(config, cfg, snapshot, ct);

        if (app.LatestRevision == previous)
        {
            // The rollout never made a new revision; restart it if its secrets went back.
            if (secretsRestored && !await client.RestartRevisionAsync(service, previous, ct))
            {
                logger.LogWarning("Secrets of {Name} were restored but revision {Revision} could not be restarted.", service, previous);
            }

            return previous;
        }

        await client.CopyRevisionAsync(service, previous, ct);
        var after = await client.GetAppAsync(service, ct);
        var restored = after?.LatestRevision
                       ?? throw new InvalidOperationException($"Azure reported no revision for '{service}' after the copy.");
        logger.LogInformation("Rolled {Name} back to {Previous} as new revision {Restored}.", service, previous, restored);
        return restored;
    }

    public bool HasNativeRollback(EnvironmentConfig config) => false;

    // ── Publish ───────────────────────────────────────────────────────────────

    public async Task PublishAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string appHostDirectory,
        CancellationToken ct = default)
        => await publisher.PublishAsync(manifest, config, appHostDirectory, ct);

    // ── Explain ───────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceExplanation> Explain(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);

        return
        [
            .. apps.Select(app => new ServiceExplanation
            {
                Name = app.Name,
                Image = app.Image,
                ImageTag = app.ImageTag,
                Count = app.MaxReplicas,
                Env = ResolvedEnvironment.ForDisplay(app.Env, app.SecretEnv),
                Ports = [.. app.Ports.Select(p => $"{p.ContainerPort}/{p.Protocol}")],
                Dependencies = app.Dependencies,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["resourceGroup"] = cfg.ResourceGroup,
                    ["location"] = cfg.Location,
                    ["environment"] = cfg.EnvironmentName,
                    ["ingress"] = app.IngressType.ToString().ToLowerInvariant(),
                    ["minReplicas"] = app.MinReplicas.ToString(),
                    ["maxReplicas"] = app.MaxReplicas.ToString(),
                    ["cpu"] = $"{app.Cpu} cores",
                    ["memory"] = app.Memory,
                    ["type"] = app.IsProjectService ? "project" : "container"
                }
            })
        ];
    }

    // ── Graph ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<ServiceDependency> GetDependencies(
        AspireManifest manifest, EnvironmentConfig config)
    {
        var cfg = GetConfig(config);
        return
        [
            .. _generator.Generate(manifest, config, cfg)
                .SelectMany(app => app.Dependencies.Select(dep =>
                    new ServiceDependency { From = dep, To = app.Name }))
        ];
    }

    // ── Diff ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var cfg = GetConfig(config);
        var apps = _generator.Generate(manifest, config, cfg);
        var client = Client(cfg);
        var entries = new List<DiffEntry>();

        HashSet<string> running;
        try
        {
            running = [.. await client.GetRunningAppNamesAsync(ct)];
        }
        catch
        {
            return
            [
                .. apps
                    .Select(a => new DiffEntry { ServiceName = a.Name, Kind = DiffKind.Added })
            ];
        }

        var desired = apps.Select(a => a.Name).ToHashSet();

        foreach (var app in apps)
        {
            if (!running.Contains(app.Name))
            {
                entries.Add(new DiffEntry { ServiceName = app.Name, Kind = DiffKind.Added });
                continue;
            }

            // Compare latest revision against desired image tag
            var revision = await client.GetLatestRevisionAsync(app.Name, ct);
            var kind = revision is null ? DiffKind.Changed : DiffKind.Unchanged;
            entries.Add(new DiffEntry
            {
                ServiceName = app.Name,
                Kind = kind,
                Field = kind == DiffKind.Changed ? "revision" : null
            });
        }

        // Only apps Deployify tagged (for this environment) are candidates for deletion.
        foreach (var name in (await RemovableAsync(client, cfg, ct)).Except(desired))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    /// <summary>
    /// Apps plan may list for deletion: tagged as Deployify's and not tagged for another
    /// environment. Empty when Azure can't be asked, so an outage never shows up as deletions.
    /// </summary>
    private async Task<IReadOnlyList<string>> RemovableAsync(AcaCliClient client, AcaTargetConfig cfg, CancellationToken ct)
    {
        try
        {
            return
            [
                .. (await client.GetManagedAppsAsync(ct))
                    .Where(m => m.Environment is null || cfg.DeployifyEnvironment is null || m.Environment == cfg.DeployifyEnvironment)
                    .Select(m => m.Name)
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not list Deployify's Container Apps: {Message}", ex.Message);
            return [];
        }
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => [.. (await Client(GetConfig(config)).GetManagedAppsAsync(ct)).Select(m => new ManagedService(m.Name, m.Id, m.Environment))];

    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        await Client(GetConfig(config)).DeleteAppAsync(service, ct);
        logger.LogInformation("Deleted Container App {Name}.", service);
    }

    public IReadOnlyList<string> DescribeKept(EnvironmentConfig config, DeleteOptions options)
    {
        var cfg = GetConfig(config);
        return
        [
            $"the resource group '{cfg.ResourceGroup}', the Container Apps environment '{cfg.EnvironmentName}' and its Log Analytics workspace",
            "Key Vault secrets (Deployify only references them)",
            "images in the registry"
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AcaTargetConfig GetConfig(EnvironmentConfig config)
    {
        var cfg = config.TargetConfig.TryGetValue("aca", out var raw) && raw is JsonElement el
            ? el.Deserialize<AcaTargetConfig>(TargetConfigJsonOptions.Default) ?? new AcaTargetConfig()
            : new AcaTargetConfig();
        cfg.DeployifyEnvironment = config.Name;
        return cfg;
    }
}
