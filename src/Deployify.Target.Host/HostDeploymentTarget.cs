using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Services;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Host;

/// <summary>
/// IDeploymentTarget for plain machines: each Aspire project is published to a folder and runs
/// as an operating-system service, a systemd unit on Linux or a Windows service on Windows, on
/// every host in <see cref="HostTargetConfig.Hosts"/>.
///
/// Output structure:
///   {outputDir}/
///     deployify-host.sh | deployify-host.ps1   ← the script that talks to the hosts (for reference)
///     {service}/
///       service.json                 ← the service: ports, environment (secret values left out)
///       deployify-{name}.service     ← Linux: the systemd unit apply installs
///       package/                     ← dotnet publish output, written by 'deployify publish'
///       package.sha256
///
/// Every release goes into its own folder on the host, named after the tag and a fingerprint of
/// the build and its settings; apply switches the service to it and restarts it, and keeps the
/// newest <see cref="HostTargetConfig.KeepReleases"/> so a rollback can switch back. With several
/// hosts, apply goes host by host and only moves on once the service is healthy where it just
/// deployed it.
/// </summary>
public sealed class HostDeploymentTarget(ILogger<HostDeploymentTarget> logger)
    : IDeploymentTarget, IRolloutTarget, IResourceLifecycle, IServiceLogSource
{
    private const string WindowsServicesAssembly = "Microsoft.Extensions.Hosting.WindowsServices.dll";
    private const string ReleaseKeyPrefix = "release@";

    private readonly HostServiceGenerator _generator = new();

    /// <summary>The services each environment last generated, for the health gate, which only gets a service name.</summary>
    private readonly ConcurrentDictionary<(string?, string), HostService> _services = new();

    /// <summary>Starts ssh, scp, bash, PowerShell and dotnet; replaceable in tests.</summary>
    internal IProcessRunner Processes { get; init; } = ProcessRunner.Default;

    /// <summary>How often apply looks at a host while waiting for it to become healthy; replaceable in tests.</summary>
    internal TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Replaces the drivers in tests.</summary>
    internal Func<HostTargetConfig, string?, IHostDriver>? DriverFactory { get; init; }

    public DeploymentTargetType TargetType => DeploymentTargetType.Host;
    public string DisplayName => "Host (systemd or Windows services)";

    private IHostDriver Driver(HostTargetConfig config, string? host)
        => DriverFactory?.Invoke(config, host) ?? (config.EffectiveOs == HostOs.Windows
            ? new WindowsHost(config, host, logger, Processes)
            : new LinuxHost(config, host, logger, Processes));

    private List<IHostDriver> Drivers(HostTargetConfig config) => [.. config.Targets.Select(h => Driver(config, h))];

    private static async Task<string?> UnavailableAsync(IEnumerable<IHostDriver> drivers, CancellationToken ct)
    {
        foreach (var driver in drivers)
        {
            if (await driver.UnavailableReasonAsync(ct) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    // ── Ping ─────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var problems = new List<string>();
        foreach (var driver in Drivers(host))
        {
            if (await driver.UnavailableReasonAsync(ct) is { } reason)
            {
                problems.Add(reason);
                continue;
            }

            try
            {
                if (await driver.CheckAsync(ct) is { Count: > 0 } found)
                {
                    problems.Add($"{driver.Where}: {string.Join("; ", found)}");
                }
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
            }
        }

        return problems.Count == 0 ? (true, null) : (false, string.Join(Environment.NewLine, problems.Distinct()) + ".");
    }

    // ── Generate ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var services = await ServicesAsync(manifest, config, outputDirectory, ct);
        return await WriteFilesAsync(services, GetConfig(config), outputDirectory, ct);
    }

    private Task<IReadOnlyList<HostService>> ServicesAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct)
        => ServicesInAppHostAsync(manifest, config, AppHostDirectory(config, outputDirectory), ct);

    /// <summary>The services, with a warning for each resource the host target won't run.</summary>
    private async Task<IReadOnlyList<HostService>> ServicesInAppHostAsync(
        AspireManifest manifest, EnvironmentConfig config, string? appHostDirectory, CancellationToken ct)
    {
        var services = await _generator.GenerateAsync(manifest, config, GetConfig(config), appHostDirectory, ct);
        foreach (var (name, reason) in _generator.Skipped)
        {
            logger.LogWarning("{Name} is not deployed to the hosts: {Reason}.", name, reason);
        }

        foreach (var service in services)
        {
            _services[(config.Name, service.Service)] = service;
        }

        return services;
    }

    private static async Task<List<string>> WriteFilesAsync(
        IReadOnlyList<HostService> services, HostTargetConfig host, string outputDirectory, CancellationToken ct)
    {
        var paths = new List<string>(await HostServiceGenerator.WriteScriptsAsync(host.EffectiveOs, outputDirectory, ct));
        foreach (var service in services)
        {
            paths.Add(await HostServiceGenerator.WriteServiceFileAsync(service, outputDirectory, ct));
            if (host.EffectiveOs == HostOs.Linux)
            {
                paths.Add(await HostServiceGenerator.WriteUnitFileAsync(service, host.EffectiveRoot, outputDirectory, ct));
            }
        }

        return paths;
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Checked without contacting the hosts: every project needs a project file, every variable a
    /// name the service manager accepts, and every other resource a place to run.
    /// </summary>
    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var services = await ServicesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(services, host, outputDirectory, ct);
        var results = new List<ValidationResult>();

        foreach (var service in services)
        {
            var errors = new List<string>();
            if (service.ProjectPath is null)
            {
                errors.Add("has no project path in the manifest");
            }
            else if (Path.IsPathRooted(service.ProjectPath) && !File.Exists(service.ProjectPath))
            {
                errors.Add($"project file not found: {service.ProjectPath}");
            }

            foreach (var key in service.Env.Keys.Concat(service.SecretEnv.Keys).Where(k => !SystemdUnit.IsValidVariableName(k)).Distinct())
            {
                errors.Add($"environment variable '{key}' has a name the service manager doesn't accept");
            }

            results.Add(new ValidationResult { ArtifactName = Path.Combine(outputDirectory, service.Service, "service.json"), Errors = errors });
        }

        foreach (var (name, reason) in _generator.Skipped.Where(s => !host.ExternalHosts.ContainsKey(s.Name)))
        {
            results.Add(new ValidationResult { ArtifactName = name, Errors = [reason] });
        }

        if (host.Hosts.Count != host.Targets.Count || host.Hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != host.Hosts.Count)
        {
            results.Add(new ValidationResult { ArtifactName = "host.hosts", Errors = ["has an empty or repeated entry"] });
        }

        if (host.EffectiveOs == HostOs.Windows && host.EffectiveRuntime.StartsWith("linux", StringComparison.OrdinalIgnoreCase) ||
            host.EffectiveOs == HostOs.Linux && host.EffectiveRuntime.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new ValidationResult
            {
                ArtifactName = "host.runtime",
                Errors = [$"{host.EffectiveRuntime} doesn't run on {host.EffectiveOs} hosts"]
            });
        }

        return results;
    }

    // ── Plan / Diff ──────────────────────────────────────────────────────────

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => PlanResult.FromDiff(await DiffAsync(manifest, config, outputDirectory, ct));

    /// <summary>
    /// Compares each service's fingerprint (settings, secret values and the published package)
    /// with the one recorded on every host. A package published after the plan shows up at
    /// apply, so run 'deployify publish' first when the plan should account for code changes.
    /// </summary>
    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var services = await ServicesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(services, host, outputDirectory, ct);

        Dictionary<string, IReadOnlyList<LiveService>> live;
        try
        {
            live = await ListAllAsync(host, ct);
        }
        catch (InvalidOperationException ex)
        {
            return [.. services.Select(s => new DiffEntry { ServiceName = s.Service, Kind = DiffKind.Unknown, Reason = ex.Message })];
        }

        var entries = new List<DiffEntry>();
        foreach (var service in services)
        {
            var onHosts = live.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.FirstOrDefault(l => l.Name.Equals(service.Name, StringComparison.OrdinalIgnoreCase)));
            var packageHash = FolderPackage.ReadPackageHash(outputDirectory, service.Service);
            if (onHosts.Values.All(l => l is null))
            {
                entries.Add(new DiffEntry { ServiceName = service.Service, Kind = DiffKind.Added });
                continue;
            }

            if (packageHash is null)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = service.Service, Kind = DiffKind.Unknown,
                    Reason = "not published here yet, so the build can't be compared ('deployify publish')"
                });
                continue;
            }

            var entry = FolderPackage.EntryPoint(FolderPackage.PackageDirectory(outputDirectory, service.Service)) ?? "";
            var fingerprint = Fingerprint(service, host, packageHash, entry);
            var release = FolderPackage.ReleaseName(service.ImageTag, fingerprint);
            var missing = onHosts.Where(kv => kv.Value is not { Installed: true }).Select(kv => kv.Key).ToList();
            var stale = onHosts.Where(kv => kv.Value is { Installed: true } l && l.Fingerprint != fingerprint).ToList();

            if (missing.Count > 0)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = service.Service, Kind = DiffKind.Changed, Field = "hosts",
                    OldValue = $"missing on {string.Join(", ", missing)}", NewValue = release
                });
            }
            else if (stale.Count > 0)
            {
                entries.Add(new DiffEntry
                {
                    ServiceName = service.Service, Kind = DiffKind.Changed, Field = "release",
                    OldValue = string.Join(", ", stale.Select(kv => kv.Value!.Release).Distinct()), NewValue = release
                });
            }
            else
            {
                entries.Add(new DiffEntry { ServiceName = service.Service, Kind = DiffKind.Unchanged });
            }
        }

        var desired = services.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in live.Values.SelectMany(l => l)
                     .Where(l => l.Environment == config.Name && !desired.Contains(l.Service))
                     .Select(l => l.Service).Distinct(StringComparer.Ordinal))
        {
            entries.Add(new DiffEntry { ServiceName = gone, Kind = DiffKind.Removed });
        }

        return entries;
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var drivers = Drivers(host);
        var services = await ServicesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(services, host, outputDirectory, ct);

        if (await UnavailableAsync(drivers, ct) is { } reason)
        {
            return [.. services.Select(s => new DeploymentResult { ServiceName = s.Service, Success = false, Error = reason })];
        }

        var results = new List<DeploymentResult>();
        foreach (var service in services)
        {
            var (release, error) = await ApplyEverywhereAsync(host, drivers, service, outputDirectory, ct);
            results.Add(error is null
                ? new DeploymentResult { ServiceName = service.Service, Success = true, DeploymentId = $"{service.UnitName}@{release}" }
                : new DeploymentResult { ServiceName = service.Service, Success = false, Error = error });
        }

        return results;
    }

    /// <summary>
    /// Deploys one service host by host. Before moving on to the next host, waits for the
    /// service to be healthy on the one it just deployed, so a broken build stops at the first.
    /// Returns the release, or an error.
    /// </summary>
    private async Task<(string? Release, string? Error)> ApplyEverywhereAsync(
        HostTargetConfig host, IReadOnlyList<IHostDriver> drivers, HostService service, string outputDirectory, CancellationToken ct)
    {
        HostRelease release;
        try
        {
            release = Release(service, host, outputDirectory);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        for (var i = 0; i < drivers.Count; i++)
        {
            var driver = drivers[i];
            try
            {
                var changed = await driver.ApplyAsync(release, ct);
                logger.LogInformation(changed ? "{Unit} on {Host} now runs release {Release}." : "{Unit} on {Host} already runs release {Release}.",
                    service.UnitName, driver.Where, release.Release);

                if (i < drivers.Count - 1 && await WaitHealthyAsync(host, driver, service, release.Release, ct) is { } unhealthy)
                {
                    var error = $"{driver.Where}: {unhealthy}; stopped before the next host ({drivers[i + 1].Where}).";
                    logger.LogError("{Name}: {Message}", service.Service, error);
                    return (release.Release, error);
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("{Name}: {Message}", service.Service, ex.Message);
                return (null, ex.Message);
            }
        }

        return (release.Release, null);
    }

    /// <summary>Polls one host until the service is healthy there. Returns why it isn't, or null when it is.</summary>
    private async Task<string?> WaitHealthyAsync(
        HostTargetConfig host, IHostDriver driver, HostService service, string release, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, host.HostHealthTimeoutSeconds));
        var watch = Stopwatch.StartNew();
        string? last = null;
        while (true)
        {
            var observation = await driver.HealthAsync(service, release, ct);
            switch (observation.State)
            {
                case HealthState.Healthy:
                    return null;
                case HealthState.Failed:
                    return observation.Detail ?? "unhealthy";
            }

            last = observation.Detail ?? last;
            if (watch.Elapsed >= timeout)
            {
                return $"not healthy after {timeout.TotalSeconds:0}s ({last ?? "no detail"})";
            }

            await Task.Delay(HealthPollInterval, ct);
        }
    }

    /// <summary>What goes to the hosts for one service: the package, its fingerprint and release name, and the secret values.</summary>
    private static HostRelease Release(HostService service, HostTargetConfig host, string outputDirectory)
    {
        var packageHash = FolderPackage.ReadPackageHash(outputDirectory, service.Service);
        var packageDir = FolderPackage.PackageDirectory(outputDirectory, service.Service);
        if (packageHash is null)
        {
            throw new InvalidOperationException($"No published package in {packageDir}. Run 'deployify publish' first.");
        }

        var entry = FolderPackage.EntryPoint(packageDir)
                    ?? throw new InvalidOperationException($"{packageDir} holds no app (no *.runtimeconfig.json). Run 'deployify publish' again.");
        var fingerprint = Fingerprint(service, host, packageHash, entry);
        return new HostRelease(
            service, fingerprint, FolderPackage.ReleaseName(service.ImageTag, fingerprint), entry, packageDir, packageHash,
            service.SecretEnv.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal));
    }

    /// <summary>
    /// SHA-256 of everything that ends up on the hosts: settings, secret values, the package and
    /// where it runs from. Machine-specific details (the project path) are left out, so a CI
    /// runner and a laptop agree on what is deployed.
    /// </summary>
    internal static string Fingerprint(HostService service, HostTargetConfig host, string packageHash, string entryPoint)
    {
        var settings = JsonSerializer.Serialize(new
        {
            service.Name,
            service.UnitName,
            service.ImageTag,
            service.Ports,
            Env = service.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            Secrets = service.SecretEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"),
            Os = host.EffectiveOs.ToString(),
            Root = host.EffectiveRoot,
            Account = host.EffectiveOs == HostOs.Windows ? host.EffectiveServiceAccount : null,
            EntryPoint = entryPoint,
            Package = packageHash
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings)));
    }

    // ── Rollout (health-gated apply, rollback) ────────────────────────────────

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => await UnavailableAsync(Drivers(GetConfig(config)), ct);

    /// <summary>
    /// Records the release each host runs now. Only release names go in the journal: their
    /// settings, secrets included, stay on the hosts next to their files.
    /// </summary>
    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var data = new Dictionary<string, string>();
        string? revision = null;
        foreach (var (where, services) in await ListAllAsync(host, ct))
        {
            if (services.FirstOrDefault(l => l.Service == service && l.Environment == config.Name) is { Installed: true, Release: { } release })
            {
                data[ReleaseKeyPrefix + where] = release;
                revision ??= release;
            }
        }

        return new ServiceSnapshot { ServiceName = service, Existed = revision is not null, Revision = revision, Data = data };
    }

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var services = await ServicesAsync(manifest, config, outputDirectory, ct);
        await WriteFilesAsync(services, host, outputDirectory, ct);
        if (services.FirstOrDefault(s => s.Service == service) is not { } svc)
        {
            return new AppliedRevision { ServiceName = service, Success = false, Error = $"{service} is not a project the host target can run." };
        }

        var (release, error) = await ApplyEverywhereAsync(host, Drivers(host), svc, outputDirectory, ct);
        return new AppliedRevision { ServiceName = service, Success = error is null, Revision = release, Error = error };
    }

    /// <summary>
    /// One look at every host: each must run <paramref name="revision"/> and answer. Failed as
    /// soon as one host fails, healthy once all are.
    /// </summary>
    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var svc = _services.TryGetValue((config.Name, service), out var known) ? known : Ref(config, host, service).AsService();
        var details = new List<string>();
        var progressing = false;
        foreach (var driver in Drivers(host))
        {
            var observation = await driver.HealthAsync(svc, revision, ct);
            switch (observation.State)
            {
                case HealthState.Failed:
                    return observation;
                case HealthState.Progressing or HealthState.RevertedByPlatform:
                    progressing = true;
                    break;
            }

            if (observation.Detail is { } detail)
            {
                details.Add(detail);
            }
        }

        var summary = details.Count == 0 ? null : string.Join("; ", details);
        return progressing ? HealthObservation.Progressing(summary) : HealthObservation.Healthy(summary);
    }

    /// <summary>
    /// Switches each host back to the release the snapshot recorded for it, with that release's
    /// settings, which the host kept. Where the service didn't exist before, this rollout's copy
    /// is removed. Releases are immutable, so the engine waits on the snapshot's own release.
    /// </summary>
    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var live = await ListAllAsync(host, ct);
        foreach (var driver in Drivers(host))
        {
            var mine = live.TryGetValue(driver.Where, out var services)
                ? services.FirstOrDefault(l => l.Service == snapshot.ServiceName && l.Environment == config.Name)
                : null;
            var reference = mine?.Ref ?? Ref(config, host, snapshot.ServiceName);
            if (snapshot.Data.TryGetValue(ReleaseKeyPrefix + driver.Where, out var previous))
            {
                if (await driver.ActivateAsync(reference, previous, ct))
                {
                    logger.LogInformation("Switched {Unit} on {Host} back to release {Release}.", reference.UnitName, driver.Where, previous);
                }
            }
            else if (mine is not null)
            {
                await driver.DeleteAsync(reference, ct);
                logger.LogInformation("Removed {Unit} from {Host}, which this rollout created.", reference.UnitName, driver.Where);
            }
        }

        return snapshot.Existed ? snapshot.Revision : null;
    }

    public Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default) => Task.FromResult(false);

    // ── Logs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The service's output on every host: the systemd journal on Linux, the Application and
    /// System event logs on Windows. With several hosts, each line names its host.
    /// </summary>
    public async IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(
        EnvironmentConfig config, ServiceLogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var drivers = Drivers(host);
        if (await UnavailableAsync(drivers, ct) is { } reason)
        {
            throw new ServiceLogsException(reason);
        }

        Dictionary<string, IReadOnlyList<LiveService>> live;
        try
        {
            live = await ListAllAsync(host, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ServiceLogsException(ex.Message, ex);
        }

        var streams = new List<IAsyncEnumerable<ServiceLogLine>>();
        foreach (var driver in drivers)
        {
            if (live[driver.Where].FirstOrDefault(l => l.Service == query.Service && l.Environment == config.Name) is { } mine)
            {
                streams.Add(driver.LogsAsync(mine.Ref, query, ct));
            }
        }

        if (streams.Count == 0)
        {
            throw new ServiceLogsException($"{query.Service} is not deployed to {string.Join(", ", drivers.Select(d => d.Where))}.");
        }

        await foreach (var line in LogLines.CombineAsync(streams, query.Follow, ct))
        {
            yield return line;
        }
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes every project to a folder for the hosts' runtime instead of building images.
    /// For Windows hosts, refuses a project that can't run as a Windows service.
    /// </summary>
    public async Task PublishAsync(
        AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var services = await _generator.GenerateAsync(manifest, config, host, appHostDirectory, ct);
        var outputDirectory = Path.Combine(appHostDirectory, config.OutputPath);
        foreach (var service in services)
        {
            if (service.ProjectPath is null)
            {
                throw new InvalidOperationException($"{service.Service} has no project path in the manifest, so it can't be published.");
            }

            var package = await FolderPackage.PublishAsync(Processes, logger, service.Service, service.ProjectPath, outputDirectory,
                host.EffectiveRuntime, host.SelfContained, extraArgs: null, ct);

            if (host.EffectiveOs == HostOs.Windows && !File.Exists(Path.Combine(package, WindowsServicesAssembly)))
            {
                throw new InvalidOperationException(
                    $"{service.Service} can't run as a Windows service: it doesn't use Microsoft.Extensions.Hosting.WindowsServices. " +
                    $"Add the package to {Path.GetFileName(service.ProjectPath)} and call builder.Services.AddWindowsService() " +
                    "(or UseWindowsService() on the host builder); the call does nothing when the app isn't started as a service.");
            }
        }
    }

    // ── Explain / Graph ──────────────────────────────────────────────────────

    /// <summary>
    /// The services the hosts will run. Resources the target can't run are logged, not listed:
    /// rollouts and the deployment inventory take their service list from here.
    /// </summary>
    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var services = await ServicesInAppHostAsync(manifest, config, config.AppHostPath, ct);
        var hosts = host.Hosts.Count == 0 ? "this machine" : string.Join(", ", host.Hosts);
        var separator = host.EffectiveOs == HostOs.Windows ? @"\" : "/";

        return
        [
            .. services.Select(s => new ServiceExplanation
            {
                Name = s.Service,
                ImageTag = s.ImageTag,
                Env = ResolvedEnvironment.ForDisplay(s.Env, s.SecretEnv),
                Ports = [.. s.Ports.Select(p => $"http:{p}")],
                Dependencies = s.DependsOn,
                AdditionalInfo = new Dictionary<string, string>
                {
                    ["hosts"] = hosts,
                    [host.EffectiveOs == HostOs.Windows ? "windowsService" : "systemdUnit"] =
                        host.EffectiveOs == HostOs.Windows ? s.UnitName : s.UnitName + ".service",
                    ["folder"] = $"{host.EffectiveRoot}{separator}{s.Name}{separator}releases",
                    ["runtime"] = $"{host.EffectiveRuntime}, {(host.SelfContained ? "self-contained" : "framework-dependent")}",
                    ["type"] = "project"
                }
            })
        ];
    }

    /// <summary>Dependencies between services; resources the hosts don't run are not part of a rollout.</summary>
    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var services = await _generator.GenerateAsync(manifest, config, GetConfig(config), config.AppHostPath, ct);
        var names = services.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        return [.. services.SelectMany(s => s.DependsOn.Where(names.Contains).Select(d => new ServiceDependency { From = d, To = s.Service }))];
    }

    // ── Lifecycle (destroy / prune) ───────────────────────────────────────────

    /// <summary>The services with Deployify's marker on any host, for every environment; the caller picks this environment's.</summary>
    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        =>
        [
            .. (await ListAllAsync(GetConfig(config), ct)).Values.SelectMany(l => l)
                .Select(l => new ManagedService(l.Service, l.UnitName, l.Environment))
                .Distinct()
        ];

    /// <summary>Stops and removes the service, and its folder with every release, on every host that has it.</summary>
    public async Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        var live = await ListAllAsync(host, ct);
        foreach (var driver in Drivers(host))
        {
            if (live[driver.Where].FirstOrDefault(l => l.Service == service && l.Environment == config.Name) is not { } mine)
            {
                continue;
            }

            foreach (var item in await driver.DeleteAsync(mine.Ref, ct))
            {
                logger.LogInformation("Removed {What} on {Host}.", item, driver.Where);
            }
        }
    }

    public Task<IReadOnlyList<string>> DescribeKeptAsync(EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
    {
        var host = GetConfig(config);
        return Task.FromResult<IReadOnlyList<string>>(
        [
            $"the root folder {host.EffectiveRoot} on each host" +
            (host.EffectiveOs == HostOs.Windows ? ", and the apps' event log sources" : "")
        ]);
    }

    /// <summary>What every host has, keyed by <see cref="IHostDriver.Where"/>. Throws when a host can't be asked.</summary>
    private async Task<Dictionary<string, IReadOnlyList<LiveService>>> ListAllAsync(HostTargetConfig host, CancellationToken ct)
    {
        var drivers = Drivers(host);
        if (await UnavailableAsync(drivers, ct) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var result = new Dictionary<string, IReadOnlyList<LiveService>>(StringComparer.Ordinal);
        foreach (var driver in drivers)
        {
            result[driver.Where] = await driver.ListAsync(ct);
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The names this environment gives <paramref name="service"/> on the hosts.</summary>
    private static ServiceRef Ref(EnvironmentConfig config, HostTargetConfig host, string service)
    {
        var name = HostServiceGenerator.SafeName(HostServiceGenerator.Prefix(config, host) + service);
        return new ServiceRef(service, name, "deployify-" + name, config.Name);
    }

    internal static HostTargetConfig GetConfig(EnvironmentConfig config)
    {
        if (config.TargetConfig.TryGetValue("host", out var raw))
        {
            if (raw is JsonElement el)
            {
                return el.Deserialize<HostTargetConfig>(TargetConfigJsonOptions.Default) ?? new HostTargetConfig();
            }

            if (raw is HostTargetConfig typed)
            {
                return typed;
            }
        }

        return new HostTargetConfig();
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

internal static class ServiceRefExtensions
{
    /// <summary>A stand-in for a service whose settings aren't known here: no ports, so the health gate only checks the process.</summary>
    public static HostService AsService(this ServiceRef r)
        => new() { Service = r.Service, Name = r.Name, UnitName = r.UnitName, Environment = r.Environment };
}
