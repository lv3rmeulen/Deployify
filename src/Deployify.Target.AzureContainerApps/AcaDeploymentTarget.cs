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
    ILogger<AcaDeploymentTarget> logger) : IDeploymentTarget, ISecretStoreProvider
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
        var client = new AcaCliClient(cfg, logger);
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
        var client = new AcaCliClient(cfg, logger);

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
        var client = new AcaCliClient(cfg, logger);
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
            ToCreate = [.. desired.Except(running).Select(n => (object)n)],
            ToUpdate = [.. desired.Intersect(running).Select(n => (object)n)],
            ToDelete = [.. running.Except(desired)],
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
        var client = new AcaCliClient(cfg, logger);
        var results = new List<DeploymentResult>();

        foreach (var app in apps)
        {
            var specPath = Path.Combine(outputDirectory, $"{app.Name}.containerapp.json");
            if (!File.Exists(specPath))
            {
                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    Success = false,
                    Error = $"App spec not found: {specPath}. Run 'deployify plan' first."
                });
                continue;
            }

            try
            {
                // Secrets first: a new revision reads its Key Vault references when it starts.
                var secretsChanged = await SyncSecretsAsync(app, config, cfg, new AzureCliKeyVaultClient(cfg.SubscriptionId), ct);
                var resourceId = await client.DeployAppAsync(specPath, app.Name, ct);
                logger.LogInformation("Deployed Container App '{Name}' → {Id}", app.Name, resourceId);

                // An unchanged spec makes no new revision, and Container Apps only re-reads
                // Key Vault references periodically, so restart to pick up new values now.
                if (secretsChanged && await client.GetLatestRevisionAsync(app.Name, ct) is { } revision &&
                    !await client.RestartRevisionAsync(app.Name, revision, ct))
                {
                    logger.LogWarning("Secrets of {Name} changed but revision {Revision} could not be restarted.", app.Name, revision);
                }

                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    DeploymentId = resourceId,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deploy Container App '{Name}'.", app.Name);
                results.Add(new DeploymentResult
                {
                    ServiceName = app.Name,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        return results;
    }

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
        var client = new AcaCliClient(cfg, logger);
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

        foreach (var name in running.Except(desired))
        {
            entries.Add(new DiffEntry { ServiceName = name, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AcaTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("aca", out var raw) && raw is JsonElement el)
        {
            return el.Deserialize<AcaTargetConfig>(TargetConfigJsonOptions.Default) ?? new AcaTargetConfig();
        }

        return new AcaTargetConfig();
    }
}
