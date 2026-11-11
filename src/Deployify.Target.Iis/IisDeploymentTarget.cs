using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Iis;

/// <summary>
/// IDeploymentTarget for IIS on Windows Server. Each Aspire project becomes an IIS site with
/// its own application pool (no managed runtime: the ASP.NET Core Module hosts it).
///
/// Output structure:
///   {outputDir}/
///     deployify-iis.ps1          ← the script that talks to IIS (for reference; apply runs its own copy)
///     {service}/
///       site.json                ← the site: bindings, environment (secret values left out)
///       package/                 ← dotnet publish output, written by 'deployify publish'
///       package.sha256
///
/// On the server, under <see cref="IisTargetConfig.SitesRoot"/>:
///   {site}/releases/{tag}-{fingerprint}/   ← one folder per release; the site points at one
///   {site}/settings/{release}.json         ← that release's settings and secrets, for rollback
///   {site}/deployify.json                  ← ownership marker: service, environment, fingerprint
///
/// Apply copies the package into a new release folder, points the site at it, and recycles
/// the pool. Environment variables are set in applicationHost.config, never in the app folder.
/// Each release's settings are kept on the server too (settings/{release}.json, Administrators
/// only), so a rollback switches back to an earlier release with the settings it ran with.
/// </summary>
public sealed class IisDeploymentTarget(ILogger<IisDeploymentTarget> logger) : IDeploymentTarget, IRolloutTarget, IResourceLifecycle
{
    private readonly IisSiteGenerator _generator = new();

    /// <summary>Starts PowerShell and dotnet; replaceable in tests.</summary>
    internal IProcessRunner Processes { get; init; } = ProcessRunner.Default;

    public DeploymentTargetType TargetType => DeploymentTargetType.Iis;
    public string DisplayName => "IIS (Windows Server)";

    private IisRemote Remote(IisTargetConfig iis) => new(iis, logger, Processes);

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var remote = Remote(GetConfig(config));
        if (await remote.UnavailableReasonAsync(ct) is { } reason)
        {
            return (false, reason);
        }

        JsonElement info;
        try
        {
            info = await remote.RunAsync("Ping", new JsonObject(), sensitive: false, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

        var problems = new List<string>();
        if (!Flag(info, "management"))
        {
            problems.Add("IIS or its management API is not installed (enable Web-Server and Web-Scripting-Tools)");
        }

        if (!Flag(info, "aspNetCore"))
        {
            problems.Add("the ASP.NET Core Module is not installed (install the ASP.NET Core Hosting Bundle)");
        }

        if (!Flag(info, "admin"))
        {
            problems.Add("the account is not an administrator, which changing IIS requires");
        }

        return problems.Count == 0
            ? (true, null)
            : (false, $"{remote.Where}: {string.Join("; ", problems)}.");
    }

    private static bool Flag(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var sites = await SitesAsync(manifest, config, outputDirectory, ct);
        return await WriteFilesAsync(sites, outputDirectory, ct);
    }

    private Task<IReadOnlyList<IisSite>> SitesAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
        => SitesInAppHostAsync(manifest, config, AppHostDirectory(config, outputDirectory), ct);

    /// <summary>The sites, with a warning for each resource IIS won't host.</summary>
    private async Task<IReadOnlyList<IisSite>> SitesInAppHostAsync(
        AspireManifest manifest, EnvironmentConfig config, string? appHostDirectory, CancellationToken ct)
    {
        var sites = await _generator.GenerateAsync(manifest, config, GetConfig(config), appHostDirectory, ct);
        foreach (var (name, reason) in _generator.Skipped)
        {
            logger.LogWarning("{Name} is not deployed to IIS: {Reason}.", name, reason);
        }

        return sites;
    }

    private static async Task<List<string>> WriteFilesAsync(IReadOnlyList<IisSite> sites, string outputDirectory, CancellationToken ct)
    {
        var paths = new List<string> { await IisSiteGenerator.WriteScriptAsync(outputDirectory, ct) };
        foreach (var site in sites)
        {
            paths.Add(await IisSiteGenerator.WriteSiteFileAsync(site, outputDirectory, ct));
        }

        return paths;
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Checked without contacting the server: every project needs an http endpoint (IIS only
    /// hosts web apps) and a project file, and every other resource needs a place to run.
    /// </summary>
    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var sites = await SitesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(sites, outputDirectory, ct);
        var results = new List<ValidationResult>();

        foreach (var site in sites)
        {
            var errors = new List<string>();
            if (!site.Bindings.Any(b => b.Port > 0 && string.IsNullOrEmpty(b.HostName)))
            {
                errors.Add("has no http endpoint; IIS can only host web apps (a worker service needs another target)");
            }

            if (site.ProjectPath is null)
            {
                errors.Add("has no project path in the manifest");
            }
            else if (Path.IsPathRooted(site.ProjectPath) && !File.Exists(site.ProjectPath))
            {
                errors.Add($"project file not found: {site.ProjectPath}");
            }

            results.Add(new ValidationResult { ArtifactName = Path.Combine(outputDirectory, site.Service, "site.json"), Errors = errors });
        }

        foreach (var (name, reason) in _generator.Skipped.Where(s => !iis.ExternalHosts.ContainsKey(s.Name)))
        {
            results.Add(new ValidationResult { ArtifactName = name, Errors = [reason] });
        }

        if (iis.HostNames.Keys.Except(sites.Select(s => s.Service)).ToList() is { Count: > 0 } unknown)
        {
            results.Add(new ValidationResult
            {
                ArtifactName = "iis.hostNames",
                Errors = [.. unknown.Select(u => $"'{u}' is not a project in the manifest")]
            });
        }

        return results;
    }

    // ── Plan / Diff ──────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));

    /// <summary>
    /// Compares each site's fingerprint (settings, secret values and the published package) with
    /// the one recorded on the server. A package published after the plan shows up at apply,
    /// so run 'deployify publish' first when the plan should account for code changes.
    /// </summary>
    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var sites = await SitesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(sites, outputDirectory, ct);

        IReadOnlyList<LiveSite> live;
        try
        {
            live = await ListLiveAsync(iis, ct);
        }
        catch (InvalidOperationException ex)
        {
            return [.. sites.Select(s => new DiffEntry { ServiceName = s.Service, Kind = DiffKind.Unknown, Reason = ex.Message })];
        }

        var entries = new List<DiffEntry>();
        foreach (var site in sites)
        {
            var current = live.FirstOrDefault(l => l.SiteName.Equals(site.SiteName, StringComparison.OrdinalIgnoreCase));
            var packageHash = IisPublisher.ReadPackageHash(outputDirectory, site.Service);
            if (current is null)
            {
                entries.Add(new DiffEntry { ServiceName = site.Service, Kind = DiffKind.Added });
            }
            else if (packageHash is null)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = site.Service, Kind = DiffKind.Unknown,
                    Reason = "not published here yet, so the build can't be compared ('deployify publish')"
                });
            }
            else
            {
                var fingerprint = Fingerprint(site, packageHash);
                entries.Add(!current.SiteExists
                    ? new DiffEntry { ServiceName = site.Service, Kind = DiffKind.Changed, Field = "site", OldValue = "missing", NewValue = site.SiteName }
                    : current.Fingerprint == fingerprint
                        ? new DiffEntry { ServiceName = site.Service, Kind = DiffKind.Unchanged }
                        : new DiffEntry
                        {
                            ServiceName = site.Service, Kind = DiffKind.Changed, Field = "release",
                            OldValue = current.Release, NewValue = ReleaseName(site, fingerprint)
                        });
            }
        }

        var desired = sites.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in live.Where(l => l.Environment == config.Name && !desired.Contains(l.Service)))
        {
            entries.Add(new DiffEntry { ServiceName = gone.Service, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var remote = Remote(iis);
        var sites = await SitesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(sites, outputDirectory, ct);

        if (await remote.UnavailableReasonAsync(ct) is { } reason)
        {
            return [.. sites.Select(s => new DeploymentResult { ServiceName = s.Service, Success = false, Error = reason })];
        }

        var results = new List<DeploymentResult>();
        foreach (var site in sites)
        {
            var (release, error) = await ApplySiteAsync(remote, site, outputDirectory, ct);
            results.Add(error is null
                ? new DeploymentResult { ServiceName = site.Service, Success = true, DeploymentId = $"{remote.Where}/{site.SiteName}@{release}" }
                : new DeploymentResult { ServiceName = site.Service, Success = false, Error = error });
        }

        return results;
    }

    /// <summary>Copies one site's package to the server and switches the site to it. Returns the release, or an error.</summary>
    private async Task<(string? Release, string? Error)> ApplySiteAsync(
        IisRemote remote, IisSite site, string outputDirectory, CancellationToken ct)
    {
        var packageHash = IisPublisher.ReadPackageHash(outputDirectory, site.Service);
        if (packageHash is null)
        {
            return (null, $"No published package in {IisPublisher.PackageDirectory(outputDirectory, site.Service)}. Run 'deployify publish' first.");
        }

        try
        {
            var fingerprint = Fingerprint(site, packageHash);
            var release = ReleaseName(site, fingerprint);
            var request = new JsonObject
            {
                ["site"] = SitePayload(site, fingerprint, release),
                ["secrets"] = new JsonObject(site.SecretEnv
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value.ToString())))),
                ["packagePath"] = Path.GetFullPath(IisPublisher.PackageDirectory(outputDirectory, site.Service))
            };

            var result = await remote.RunAsync("Apply", request, sensitive: true, ct);
            var changed = !result.TryGetProperty("changed", out var c) || c.ValueKind != JsonValueKind.False;
            logger.LogInformation(changed ? "{Site} now runs release {Release}." : "{Site} already runs release {Release}.",
                site.SiteName, release);
            return (release, null);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError("{Name}: {Message}", site.Service, ex.Message);
            return (null, ex.Message);
        }
    }

    // ── Rollout (health-gated apply, rollback) ────────────────────────────────

    private const string SiteNameKey = "siteName";

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await Remote(GetConfig(config)).UnavailableReasonAsync(ct);

    /// <summary>
    /// Records the release the site runs now. Only its name goes in the journal: the release's
    /// settings, secrets included, stay on the server next to its files.
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var current = (await ListLiveAsync(iis, ct)).FirstOrDefault(l => l.Service == service && l.Environment == config.Name);
        var siteName = current?.SiteName ?? IisSiteGenerator.SitePrefix(config, iis) + service;
        return current is { SiteExists: true, Release: { } release }
            ? new ServiceSnapshot { ServiceName = service, Existed = true, Revision = release, Data = { [SiteNameKey] = siteName } }
            : new ServiceSnapshot { ServiceName = service, Existed = false, Data = { [SiteNameKey] = siteName } };
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
    {
        var sites = await SitesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(sites, outputDirectory, ct);
        if (sites.FirstOrDefault(s => s.Service == service) is not { } site)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = $"{service} is not a project IIS can host." };
        }

        var (release, error) = await ApplySiteAsync(Remote(GetConfig(config)), site, outputDirectory, ct);
        return new AppliedRevision { ServiceName = service, Success = error is null, Revision = release, Error = error };
    }

    /// <summary>
    /// One look, from the server: the site must run <paramref name="revision"/>, its pool and site
    /// must be started, and a request to <see cref="IisTargetConfig.HealthPath"/> must get a
    /// response below 500. The ASP.NET Core Module's start-up failure pages fail at once.
    /// </summary>
    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var request = new JsonObject
        {
            ["siteName"] = await SiteNameAsync(config, iis, service, ct),
            ["release"] = revision,
            ["healthPath"] = string.IsNullOrWhiteSpace(iis.HealthPath) ? "/" : iis.HealthPath
        };

        var result = await Remote(iis).RunAsync("Health", request, sensitive: false, ct);
        var detail = result.TryGetProperty("detail", out var d) ? d.GetString() : null;
        return (result.TryGetProperty("state", out var s) ? s.GetString() : null) switch
        {
            "healthy" => HealthObservation.Healthy(detail),
            "failed" => HealthObservation.Failed(detail ?? "unhealthy"),
            _ => HealthObservation.Progressing(detail)
        };
    }

    /// <summary>
    /// Switches the site back to the release the snapshot recorded, with that release's settings,
    /// which the server kept. A site this rollout created is removed. Releases are immutable,
    /// so the engine waits on the snapshot's own release.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var remote = Remote(iis);
        var siteName = snapshot.Data.TryGetValue(SiteNameKey, out var name)
            ? name
            : await SiteNameAsync(config, iis, snapshot.ServiceName, ct);

        if (snapshot is not { Existed: true, Revision: { } previous })
        {
            await remote.RunAsync("Delete", new JsonObject { ["siteName"] = siteName }, sensitive: false, ct);
            logger.LogInformation("Removed {Site}, which this rollout created.", siteName);
            return null;
        }

        var result = await remote.RunAsync("Activate", new JsonObject { ["siteName"] = siteName, ["release"] = previous }, sensitive: false, ct);
        var changed = !result.TryGetProperty("changed", out var c) || c.ValueKind != JsonValueKind.False;
        if (changed)
        {
            logger.LogInformation("Switched {Site} back to release {Release}.", siteName, previous);
        }

        return previous;
    }

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>The site this environment deployed for <paramref name="service"/>, or the name it would get.</summary>
    private async Task<string> SiteNameAsync(EnvironmentConfig config, IisTargetConfig iis, string service, CancellationToken ct)
        => (await ListLiveAsync(iis, ct)).FirstOrDefault(l => l.Service == service && l.Environment == config.Name)?.SiteName
           ?? IisSiteGenerator.SitePrefix(config, iis) + service;

    /// <summary>What the server needs to set the site up: site.json plus the fingerprint and release folder.</summary>
    internal static JsonNode SitePayload(IisSite site, string fingerprint, string release)
    {
        var node = JsonSerializer.SerializeToNode(site, IisSiteGenerator.JsonOptions)!.AsObject();
        node["fingerprint"] = fingerprint;
        node["release"] = release;
        return node;
    }

    /// <summary>
    /// SHA-256 of everything that ends up on the server: settings, secret values and the package.
    /// Machine-specific details (the project path) are left out, so a CI runner and a laptop
    /// agree on what is deployed.
    /// </summary>
    internal static string Fingerprint(IisSite site, string packageHash)
    {
        var settings = JsonSerializer.Serialize(new
        {
            site.SiteName,
            site.ImageTag,
            Bindings = site.Bindings.Select(b => $"{b.Protocol}:{b.Port}:{b.HostName}"),
            Env = site.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            Secrets = site.SecretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"),
            site.AppPoolIdentity,
            site.AlwaysRunning,
            site.CertificateThumbprint,
            site.CertificateStore,
            Package = packageHash
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings)));
    }

    /// <summary>Release folder name: the tag (made safe for a folder name) and the start of the fingerprint.</summary>
    internal static string ReleaseName(IisSite site, string fingerprint)
    {
        var tag = new string([.. site.ImageTag.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')]);
        return $"{(tag.Length == 0 ? "release" : tag)}-{fingerprint[..12]}";
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    /// <summary>Publishes every project to a folder for Windows instead of building images.</summary>
    public async Task PublishAsync(
        AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var sites = await _generator.GenerateAsync(manifest, config, iis, appHostDirectory, ct);
        await new IisPublisher(logger, Processes).PublishAsync(sites, iis, Path.Combine(appHostDirectory, config.OutputPath), ct);
    }

    // ── Explain / Graph ──────────────────────────────────────────────────────

    /// <summary>
    /// The sites IIS will host. Resources it can't host are logged, not listed: rollouts and the
    /// deployment inventory take their service list from here.
    /// </summary>
    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var sites = await SitesInAppHostAsync(manifest, config, config.AppHostPath, ct);
        var server = string.IsNullOrWhiteSpace(iis.Host) ? "this machine" : iis.Host;

        return
        [
            .. sites.Select(site => new ServiceExplanation
            {
                Name = site.Service,
                ImageTag = site.ImageTag,
                Env = ResolvedEnvironment.ForDisplay(site.Env, site.SecretEnv),
                Ports = [.. site.Bindings.Select(b => string.IsNullOrEmpty(b.HostName) ? $"{b.Protocol}:{b.Port}" : $"{b.Protocol}://{b.HostName}:{b.Port}")],
                Dependencies = site.DependsOn,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["server"] = server,
                    ["site"] = site.SiteName,
                    ["appPool"] = $"{site.SiteName} ({site.AppPoolIdentity}, {(site.AlwaysRunning ? "always running" : "on demand")})",
                    ["folder"] = $@"{iis.SitesRoot.TrimEnd('\\')}\{site.SiteName}\releases",
                    ["type"] = "project"
                }
            })
        ];
    }

    /// <summary>Dependencies between sites; resources IIS doesn't host are not part of a rollout.</summary>
    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var sites = await _generator.GenerateAsync(manifest, config, GetConfig(config), config.AppHostPath, ct);
        var names = sites.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        return [.. sites.SelectMany(s => s.DependsOn.Where(names.Contains).Select(d => new ServiceDependency { From = d, To = s.Service }))];
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>
    /// The service folders under the sites root with Deployify's marker file, for every
    /// environment on the server; the caller picks this environment's.
    /// </summary>
    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => [.. (await ListLiveAsync(GetConfig(config), ct)).Select(l => new ManagedService(l.Service, l.SiteName, l.Environment))];

    /// <summary>Removes the site, its application pool, its settings and its folder with every release.</summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var iis = GetConfig(config);
        var siteName = await SiteNameAsync(config, iis, service, ct);
        var result = await Remote(iis).RunAsync("Delete", new JsonObject { ["siteName"] = siteName }, sensitive: false, ct);
        if (!result.TryGetProperty("removed", out var removed))
        {
            return;
        }

        // Windows PowerShell 5.1 writes a one-item array as the item itself.
        var items = removed.ValueKind switch
        {
            JsonValueKind.Array => removed.EnumerateArray().Select(i => i.GetString()).ToList(),
            JsonValueKind.String => [removed.GetString()],
            _ => []
        };
        foreach (var item in items)
        {
            logger.LogInformation("Removed {What}.", item);
        }
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(["IIS, the ASP.NET Core Hosting Bundle and the sites root folder on the server"]);

    internal sealed record LiveSite(string Service, string SiteName, string? Environment, string? Fingerprint, string? Release, bool SiteExists);

    private async Task<IReadOnlyList<LiveSite>> ListLiveAsync(IisTargetConfig iis, CancellationToken ct)
    {
        var remote = Remote(iis);
        if (await remote.UnavailableReasonAsync(ct) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var result = await remote.RunAsync("List", new JsonObject(), sensitive: false, ct);
        return ParseLive(result);
    }

    internal static IReadOnlyList<LiveSite> ParseLive(JsonElement result)
    {
        if (!result.TryGetProperty("services", out var services))
        {
            return [];
        }

        // ConvertTo-Json writes a one-item array as the item itself in Windows PowerShell 5.1.
        var items = services.ValueKind switch
        {
            JsonValueKind.Array => services.EnumerateArray().ToList(),
            JsonValueKind.Object => [services],
            _ => []
        };

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        return
        [
            .. items
                .Where(i => Str(i, "service") is not null && Str(i, "siteName") is not null)
                .Select(i => new LiveSite(
                    Str(i, "service")!, Str(i, "siteName")!, Str(i, "environment"), Str(i, "fingerprint"), Str(i, "release"),
                    i.TryGetProperty("siteExists", out var e) && e.ValueKind == JsonValueKind.True))
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static IisTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("iis", out var raw))
        {
            if (raw is JsonElement el)
            {
                return el.Deserialize<IisTargetConfig>(TargetConfigJsonOptions.Default) ?? new IisTargetConfig();
            }

            if (raw is IisTargetConfig typed)
            {
                return typed;
            }
        }

        return new IisTargetConfig();
    }

    /// <summary>
    /// The AppHost directory, for resolving project paths: the one init recorded, or the
    /// output folder's parent when the output path is a single relative folder.
    /// </summary>
    private static string? AppHostDirectory(EnvironmentConfig config, string outputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(config.AppHostPath))
        {
            return config.AppHostPath;
        }

        var output = config.OutputPath.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(config.OutputPath) || output.Contains('/'))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(outputDirectory));
    }
}
