using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.DigitalOcean;

/// <summary>
/// Deploys an Aspire app to DigitalOcean App Platform as one app: projects and containers become
/// services and workers, Postgres and Redis containers become Managed Databases.
///
/// Output: <c>{outputDir}/{app}.app.json</c>, the app spec with secret values masked.
///
/// Plan compares the spec with the running app and asks App Platform's propose endpoint to
/// validate it and price it. Apply creates any database clusters it needs, then creates or
/// updates the app and waits for the deployment to go live.
/// </summary>
public sealed class DoDeploymentTarget(IPublisher publisher, ILogger<DoDeploymentTarget> logger)
    : IDeploymentTarget, IResourceLifecycle, IServiceLogSource
{
    public const string Id = "digitalocean";

    /// <summary>Tag on every database cluster Deployify creates.</summary>
    public const string OwnerTag = "deployify";

    private readonly AppSpecGenerator _generator = new();

    public DeploymentTargetType TargetType { get; } = DeploymentTargetType.Parse(Id);

    public string DisplayName => "DigitalOcean App Platform";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDigitalOceanApi> _clients = new(StringComparer.Ordinal);

    /// <summary>Creates the API client for a token; tests swap in a fake.</summary>
    internal Func<string, IDigitalOceanApi> ApiFactory { get; init; } = token => new DigitalOceanApi(token);

    /// <summary>Reads environment variables (the token, registry credentials); replaceable in tests.</summary>
    internal Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>How often apply checks on a deployment or a new cluster.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        if (cfg.ReadToken(GetVariable) is not { } token)
        {
            return (false, MissingToken(cfg));
        }

        var error = await Client(token).PingAsync(ct);
        return (error is null, error);
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var app = await GenerateAppAsync(manifest, config, ct);
        foreach (var problem in app.Problems)
        {
            logger.LogWarning("{Problem}", problem);
        }

        return [await AppSpecGenerator.WriteAsync(app, outputDirectory, ct)];
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var app = await GenerateAppAsync(manifest, config, ct);
        var path = await AppSpecGenerator.WriteAsync(app, outputDirectory, ct);
        var errors = new List<string>(app.Problems);
        errors.AddRange(app.Unresolved.Select(u => $"unresolved placeholder {u}"));

        // App Platform's own check needs the API; without a token the local checks are all there is.
        if (errors.Count == 0 && cfg.ReadToken(GetVariable) is { } token)
        {
            var api = Client(token);
            try
            {
                var existing = await api.FindAppAsync(app.Spec.Name, ct);
                await api.ProposeAsync(app.Spec.ToJson(), existing?.Id, ct);
            }
            catch (DoApiException ex) when (ex.IsRejection)
            {
                errors.Add(ex.Message);
            }
            catch (Exception ex) when (ex is DoApiException or System.Net.Http.HttpRequestException)
            {
                logger.LogWarning("App Platform couldn't check the spec ({Message}); only the local checks ran.", ex.Message);
            }
        }
        else if (errors.Count == 0)
        {
            logger.LogInformation("{Variable} isn't set, so only the local checks ran; App Platform didn't check the spec.", cfg.TokenVariable);
        }

        return [new ValidationResult { ArtifactName = path, Errors = errors }];
    }

    // ── Plan ──────────────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var app = await GenerateCheckedAsync(manifest, config, ct);
        await AppSpecGenerator.WriteAsync(app, outputDirectory, ct);

        var api = Api(cfg);
        var existing = await api.FindAppAsync(app.Spec.Name, ct);
        var desired = app.Spec.ToJson();
        var plan = PlanResult.FromDiff(Collapse(SpecDiff.Compare(desired, existing?.Spec, ResourceNames(app))));

        await ReportCostAsync(api, app, desired, existing, ct);
        await ReportClustersAsync(api, app, ct);
        return plan;
    }

    /// <summary>
    /// Logs what App Platform says the app will cost. A spec App Platform rejects fails the plan:
    /// apply would fail the same way.
    /// </summary>
    private async Task ReportCostAsync(IDigitalOceanApi api, GeneratedApp app, JsonObject desired, DoApp? existing, CancellationToken ct)
    {
        DoProposal proposal;
        try
        {
            proposal = await api.ProposeAsync(desired, existing?.Id, ct);
        }
        catch (DoApiException ex) when (ex.IsRejection)
        {
            throw new InvalidOperationException($"App Platform rejects the app spec: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is DoApiException or System.Net.Http.HttpRequestException)
        {
            logger.LogWarning("Couldn't get a cost estimate from App Platform: {Message}", ex.Message);
            return;
        }

        if (existing is null && proposal.NameAvailable == false)
        {
            throw new InvalidOperationException(
                $"The app name '{app.Spec.Name}' is taken. Set TargetConfig.digitalocean.AppName to another name" +
                (proposal.NameSuggestion is { Length: > 0 } suggestion ? $", such as '{suggestion}'." : "."));
        }

        if (proposal.AppCost is { } cost)
        {
            logger.LogInformation("App Platform estimate for '{App}': {Cost} per month{Note}.",
                app.Spec.Name, Money(cost),
                app.Databases.Any(d => d.Mode == DoDatabaseMode.Cluster)
                    ? ", not counting Managed Database clusters (billed separately)"
                    : "");
        }
    }

    private async Task ReportClustersAsync(IDigitalOceanApi api, GeneratedApp app, CancellationToken ct)
    {
        foreach (var db in app.Databases.Where(d => d.Mode == DoDatabaseMode.Cluster))
        {
            var cluster = await api.FindClusterAsync(db.ClusterName!, ct);
            if (cluster is not null)
            {
                continue;
            }

            if (db.Create)
            {
                logger.LogInformation(
                    "Apply will create Managed Database cluster '{Cluster}' ({Engine}, {Size}, {Nodes} node(s)) for {Resource}.",
                    db.ClusterName, db.ApiEngine, db.Size, db.Nodes, db.Resource);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{db.Resource}: Managed Database cluster '{db.ClusterName}' doesn't exist and Databases.{db.Resource}.Create is false.");
            }
        }
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var app = await GenerateCheckedAsync(manifest, config, ct);
        await AppSpecGenerator.WriteAsync(app, outputDirectory, ct);
        var api = Api(cfg);

        var clusters = await EnsureClustersAsync(api, app, cfg, ct);
        var spec = app.Spec.ToJson();
        var existing = await api.FindAppAsync(app.Spec.Name, ct);

        DoApp deployed;
        if (existing is null)
        {
            deployed = await api.CreateAppAsync(spec, cfg.ProjectId, ct);
            logger.LogInformation("Created App Platform app '{App}' ({Id}).", app.Spec.Name, deployed.Id);
            await AllowAppAsync(api, clusters, deployed.Id, ct);
        }
        else
        {
            // Trust first, so the new deployment can reach a cluster that was just attached.
            await AllowAppAsync(api, clusters, existing.Id, ct);
            deployed = await api.UpdateAppAsync(existing.Id, spec, ct);
            logger.LogInformation("Updated App Platform app '{App}' ({Id}).", app.Spec.Name, deployed.Id);
        }

        var deployment = await WaitForDeploymentAsync(api, deployed, cfg, ct);
        var error = deployment switch
        {
            null => null,
            { Succeeded: true } => null,
            { IsFinished: true } => $"deployment {deployment.Id} ended in {deployment.Phase}" +
                                    (deployment.Cause is null ? "" : $" ({deployment.Cause})") +
                                    ". See the app's activity log in the DigitalOcean control panel.",
            _ => $"deployment {deployment.Id} was still {deployment.Phase} after {cfg.DeployTimeoutMinutes} minutes; " +
                 "it may still finish. Check the app in the DigitalOcean control panel."
        };

        if (error is null && deployed.LiveUrl is { } url)
        {
            logger.LogInformation("'{App}' is live at {Url}.", app.Spec.Name, url);
        }

        return
        [
            .. app.Components.Select(c => new DeploymentResult
            {
                ServiceName = c.Resource,
                DeploymentId = deployment?.Id ?? deployed.Id,
                Success = error is null,
                Error = error
            })
        ];
    }

    /// <summary>
    /// Makes sure every attached cluster exists (creating it when allowed), is online and has
    /// the databases the app names. Returns the clusters by resource.
    /// </summary>
    private async Task<List<DoCluster>> EnsureClustersAsync(IDigitalOceanApi api, GeneratedApp app, DoTargetConfig cfg, CancellationToken ct)
    {
        var result = new List<DoCluster>();
        foreach (var db in app.Databases.Where(d => d.Mode == DoDatabaseMode.Cluster))
        {
            var cluster = await api.FindClusterAsync(db.ClusterName!, ct);
            if (cluster is null)
            {
                if (!db.Create)
                {
                    throw new InvalidOperationException(
                        $"{db.Resource}: Managed Database cluster '{db.ClusterName}' doesn't exist and Databases.{db.Resource}.Create is false.");
                }

                var region = DoTargetConfig.DatacenterFor(cfg.Region) ?? throw new InvalidOperationException(
                    $"{db.Resource}: set TargetConfig.digitalocean.Region so Deployify knows where to create cluster '{db.ClusterName}'; " +
                    "an app can only reach databases in its own region's datacenter.");
                cluster = await api.CreateClusterAsync(new DoClusterRequest(
                    db.ClusterName!, db.ApiEngine, db.Version, region, db.Size, db.Nodes,
                    [OwnerTag, AppTag(app.Spec.Name)]), ct);
                logger.LogInformation("Creating Managed Database cluster '{Cluster}' in {Region}; this usually takes several minutes.",
                    db.ClusterName, region);
            }
            else if (!string.Equals(cluster.Engine, db.ApiEngine, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{db.Resource}: cluster '{db.ClusterName}' runs {cluster.Engine}, not {db.ApiEngine}. " +
                    $"Set Databases.{db.Resource}.ClusterName to a {db.ApiEngine} cluster, or Databases.{db.Resource}.Engine to match it.");
            }

            cluster = await WaitForClusterAsync(api, cluster, cfg, ct);

            if (db.IsPostgres && db.DatabaseNames.Count > 0)
            {
                var existing = await api.ListClusterDatabasesAsync(cluster.Id, ct);
                foreach (var name in db.DatabaseNames.Where(n => !existing.Contains(n)))
                {
                    await api.CreateClusterDatabaseAsync(cluster.Id, name, ct);
                    logger.LogInformation("Created database '{Database}' in cluster '{Cluster}'.", name, db.ClusterName);
                }
            }

            result.Add(cluster);
        }

        return result;
    }

    private async Task<DoCluster> WaitForClusterAsync(IDigitalOceanApi api, DoCluster cluster, DoTargetConfig cfg, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(cfg.DatabaseTimeoutMinutes);
        while (cluster.Status != "online")
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Cluster '{cluster.Name}' was still {cluster.Status} after {cfg.DatabaseTimeoutMinutes} minutes. " +
                    "Run apply again once it is online.");
            }

            await Task.Delay(PollInterval, ct);
            cluster = await api.GetClusterAsync(cluster.Id, ct)
                      ?? throw new InvalidOperationException($"Cluster '{cluster.Name}' disappeared while Deployify waited for it.");
        }

        return cluster;
    }

    private async Task AllowAppAsync(IDigitalOceanApi api, List<DoCluster> clusters, string appId, CancellationToken ct)
    {
        foreach (var cluster in clusters)
        {
            await api.AllowAppAsync(cluster.Id, appId, ct);
        }
    }

    private async Task<DoDeployment?> WaitForDeploymentAsync(IDigitalOceanApi api, DoApp app, DoTargetConfig cfg, CancellationToken ct)
    {
        var deployment = app.PendingDeploymentId is { } id
            ? await api.GetDeploymentAsync(app.Id, id, ct)
            : await api.GetLatestDeploymentAsync(app.Id, ct);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(cfg.DeployTimeoutMinutes);
        var lastPhase = "";
        while (deployment is { IsFinished: false } && DateTimeOffset.UtcNow < deadline)
        {
            if (deployment.Phase != lastPhase)
            {
                logger.LogInformation("Deployment {Id}: {Phase}", deployment.Id, deployment.Phase);
                lastPhase = deployment.Phase;
            }

            await Task.Delay(PollInterval, ct);
            deployment = await api.GetDeploymentAsync(app.Id, deployment.Id, ct) ?? deployment;
        }

        return deployment;
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
    {
        if (manifest.Resources.Values.Any(r => r.IsDeployable && r.IsBuiltImage))
        {
            if (string.IsNullOrWhiteSpace(config.ContainerRegistry))
            {
                throw new InvalidOperationException(
                    $"Set ContainerRegistry to {DoImage.DocrHost}/<your-registry> (or a ghcr.io or Docker Hub repository) " +
                    "so App Platform can pull the images Deployify builds. Log in first with 'doctl registry login'.");
            }

            if (DoImage.ToSource($"{config.ContainerRegistry.TrimEnd('/')}/probe", null, out var error) is null)
            {
                throw new InvalidOperationException(error!.Replace("/probe", "", StringComparison.Ordinal));
            }
        }

        return publisher.PublishAsync(manifest, config, appHostDirectory, ct);
    }

    // ── Explain, graph, diff ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var app = await GenerateAppAsync(manifest, config, ct);
        return
        [
            .. app.Components.Select(c =>
            {
                var info = new Dictionary<string, string> { ["app"] = app.Spec.Name, ["component"] = Describe(c, app) };
                if (c.Kind != ComponentKind.Database)
                {
                    info["instanceSize"] = c.InstanceSize ?? "";
                }

                var (image, tag) = c.Image is null ? (null, null) : SplitForDisplay(c.Image);
                return new ServiceExplanation
                {
                    Name = c.Resource,
                    Image = image,
                    ImageTag = tag,
                    Count = c.Kind == ComponentKind.Database ? 1 : c.InstanceCount,
                    Env = c.DisplayEnv,
                    Ports = c.Ports,
                    Dependencies = c.Dependencies,
                    AdditionalInfo = info
                };
            })
        ];
    }

    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var app = await GenerateAppAsync(manifest, config, ct);
        var known = app.Components.Select(c => c.Resource).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. app.Components.SelectMany(c => c.Dependencies
                .Where(known.Contains)
                .Select(d => new ServiceDependency { From = c.Resource, To = d }))
        ];
    }

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var app = await GenerateCheckedAsync(manifest, config, ct);
        var existing = await Api(cfg).FindAppAsync(app.Spec.Name, ct);
        return SpecDiff.Compare(app.Spec.ToJson(), existing?.Spec, ResourceNames(app));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var app = await Api(cfg).FindAppAsync(cfg.EffectiveAppName, ct);
        return app is null
            ? []
            : [.. ComponentNames(app.Spec).Select(name => new ManagedService(name, Id, config.Name))];
    }

    /// <summary>
    /// Removes the component from the app. An app can't be left without a service or worker,
    /// so removing the last one deletes the app.
    /// </summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var api = Api(cfg);
        var app = await api.FindAppAsync(cfg.EffectiveAppName, ct);
        var component = Names.Component(service);
        if (app is null || !ComponentNames(app.Spec).Contains(component))
        {
            return;
        }

        var spec = app.Spec.DeepClone().AsObject();
        foreach (var array in new[] { "services", "workers", "jobs", "databases" })
        {
            if (spec[array] is JsonArray items)
            {
                foreach (var item in items.Where(i => i?["name"]?.GetValue<string>() == component).ToList())
                {
                    items.Remove(item);
                }
            }
        }

        if (spec["ingress"]?["rules"] is JsonArray rules)
        {
            foreach (var rule in rules.Where(r => r?["component"]?["name"]?.GetValue<string>() == component).ToList())
            {
                rules.Remove(rule);
            }
        }

        var runnable = new[] { "services", "workers", "jobs", "static_sites", "functions" }
            .Sum(a => spec[a] is JsonArray items ? items.Count : 0);
        if (runnable == 0)
        {
            await api.DeleteAppAsync(app.Id, ct);
            logger.LogInformation("Deleted App Platform app '{App}': {Service} was its last component.", app.Name, service);
            return;
        }

        await api.UpdateAppAsync(app.Id, spec, ct);
        logger.LogInformation("Removed {Service} from App Platform app '{App}'.", service, app.Name);
    }

    public async Task<IReadOnlyList<string>> DestroyEnvironmentAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var api = Api(cfg);
        var lines = new List<string>();

        if (await api.FindAppAsync(cfg.EffectiveAppName, ct) is { } app)
        {
            await api.DeleteAppAsync(app.Id, ct);
            lines.Add($"App Platform app '{app.Name}' (and its dev databases)");
        }

        // Only clusters Deployify created for this app carry its tag; attached ones are never touched.
        if (options.RemoveVolumes)
        {
            foreach (var cluster in await api.ListClustersAsync(AppTag(cfg.EffectiveAppName), ct))
            {
                await api.DeleteClusterAsync(cluster.Id, ct);
                lines.Add($"Managed Database cluster '{cluster.Name}' and all its data");
            }
        }

        return lines;
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var kept = new List<string>();
        if (!options.RemoveVolumes)
        {
            kept.Add("Managed Database clusters, with their data (pass --volumes to delete the ones Deployify created)");
        }
        else
        {
            kept.Add("Managed Database clusters Deployify didn't create");
        }

        kept.Add($"images in the container registry{(string.IsNullOrEmpty(config.ContainerRegistry) ? "" : $" {config.ContainerRegistry}")}");
        return Task.FromResult<IReadOnlyList<string>>(kept);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<GeneratedApp> GenerateAppAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct)
    {
        var app = await _generator.GenerateAsync(manifest, config, DoTargetConfig.From(config), GetVariable, ct);
        foreach (var warning in app.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        return app;
    }

    /// <summary>Generates the app and refuses to go on when the spec can't be deployed as is.</summary>
    private async Task<GeneratedApp> GenerateCheckedAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct)
    {
        var app = await GenerateAppAsync(manifest, config, ct);
        if (app.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The app can't be deployed to App Platform as configured:" + Environment.NewLine +
                string.Join(Environment.NewLine, app.Problems.Select(p => "  - " + p)));
        }

        return app;
    }

    private IDigitalOceanApi Api(DoTargetConfig cfg)
        => Client(cfg.ReadToken(GetVariable) ?? throw new InvalidOperationException(MissingToken(cfg)));

    /// <summary>One client per token for the life of the plugin, so connections are reused.</summary>
    private IDigitalOceanApi Client(string token) => _clients.GetOrAdd(token, ApiFactory);

    private static string MissingToken(DoTargetConfig cfg)
        => $"Set {cfg.TokenVariable} to a DigitalOcean API token with read and write access to apps and databases " +
           "(create one at https://cloud.digitalocean.com/account/api/tokens).";

    internal static string AppTag(string appName) => $"deployify-app-{appName}";

    // ── Logs ─────────────────────────────────────────────────────────────────

    /// <summary>The run logs of the service's component in the environment's app.</summary>
    public async IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(
        EnvironmentConfig config, ServiceLogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cfg = DoTargetConfig.From(config);
        var api = Api(cfg);
        var app = await api.FindAppAsync(cfg.EffectiveAppName, ct)
                  ?? throw new ServiceLogsException(
                      $"There is no App Platform app '{cfg.EffectiveAppName}'. Is this environment deployed?");
        await foreach (var line in DoLogs.ReadAsync(
                           api, app.Name, ComponentNames(app.Spec).ToHashSet(StringComparer.Ordinal), app.Id, query, ct))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> ComponentNames(JsonObject spec)
        => new[] { "services", "workers", "jobs", "static_sites", "functions", "databases" }
            .SelectMany(a => spec[a]?.AsArray() ?? [])
            .Select(c => c?["name"]?.GetValue<string>())
            .OfType<string>();

    private static Dictionary<string, string> ResourceNames(GeneratedApp app)
        => app.Components.ToDictionary(c => c.Component, c => c.Resource, StringComparer.Ordinal);

    /// <summary>One plan line per service: several changed fields of one component are one update.</summary>
    private static IEnumerable<DiffEntry> Collapse(IEnumerable<DiffEntry> entries)
        => entries.GroupBy(e => e.ServiceName).Select(g => g.First());

    private static string Describe(GeneratedComponent c, GeneratedApp app) => c.Kind switch
    {
        ComponentKind.PublicService => $"service, public at {c.Route}",
        ComponentKind.InternalService => "service, internal only",
        ComponentKind.Worker => "worker",
        _ => app.Databases.First(d => d.Resource == c.Resource) is var db && db.Mode == DoDatabaseMode.Cluster
            ? $"Managed Database cluster '{db.ClusterName}' ({db.Engine}{(db.Version is null ? "" : " " + db.Version)})"
            : $"dev database ({db.Engine}{(db.Version is null ? "" : " " + db.Version)})"
    };

    private static (string Image, string? Tag) SplitForDisplay(string image)
    {
        var (name, tag, digest) = DoImage.Split(image);
        return (name, tag ?? digest);
    }

    private static string Money(decimal amount) => amount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
