using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Target.Nomad.Api;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>Generates Nomad job files (the job API's JSON) from an <see cref="AspireManifest"/>.</summary>
/// <remarks>
/// Every Docker task runs with host networking and a fixed port. A reference to another
/// service gets that service's address from the service catalog at runtime (see
/// <see cref="ServiceDiscovery"/>), so services can run on different nodes. With
/// <c>registerServices: false</c> nothing is registered to look up, and services reach
/// each other on <c>localhost:&lt;port&gt;</c>, which only works on a single node.
/// For mTLS between services use the NomadConsul target, which routes through Consul Connect.
/// </remarks>
public sealed class JobGenerator(ILogger<JobGenerator> logger)
{
    public async Task<IReadOnlyList<object>> GenerateJobsAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => [.. await GenerateAsync(manifest, config, ct)];

    public async Task<string> WriteJobAsync(
        object jobObj, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => await WriteJobAsync((NomadJob)jobObj, config, outputDirectory, ct);

    // ── Model generation ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<NomadJob>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
    {
        var nc = ReadConfig(config);
        var ports = ServicePorts.Assign(manifest, nc.BasePort);
        // Only services that get a service {} block can be looked up; anything else stays on localhost.
        var discoverable = nc.RegisterServices
            ? manifest.Resources
                .Where(r => r.Value.IsDeployable && r.Value.Bindings is { Count: > 0 })
                .Select(r => r.Key)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var resolver = new PlaceholderResolver(manifest, ports,
            new EndpointStrategy { Host = n => discoverable.Contains(n) ? ServiceDiscovery.Token(n) : "localhost" },
            ParameterResolution.For(config));

        var jobs = new List<NomadJob>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var job = new NomadJob
            {
                Name = name,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Count = overrides?.Count ?? 1,
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
                IsProject = resource.IsProject,
                UseHostNetwork = nc.DeploymentMode == NomadDeploymentMode.Docker
            };

            job.Image = ServiceImage.For(name, resource, config);

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            // Values with another service's address are written by the template, which looks it up.
            job.Env = env.Plain.Where(kv => !ServiceDiscovery.HasReference(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            job.DiscoveredEnv = env.Plain.Where(kv => ServiceDiscovery.HasReference(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            job.SecretEnv = env.Secrets;

            var argsHaveSecret = false;
            foreach (var arg in ReadArgs(resource))
            {
                var resolved = await resolver.Expressions.ResolveAsync(arg, ct);
                var text = resolved.ToString();
                if (ServiceDiscovery.HasReference(text))
                {
                    // Nomad renders templates into files and env vars, not into args.
                    logger.LogWarning(
                        "An argument of {Service} refers to another service's address; it is set to localhost, " +
                        "which only works on a single node. Pass it as an environment variable instead.", name);
                    text = ServiceDiscovery.Replace(text, _ => "localhost");
                }

                job.Args.Add(text);
                argsHaveSecret |= resolved.IsSecret;
            }

            if (argsHaveSecret)
            {
                logger.LogWarning(
                    "An argument of {Service} contains a secret parameter; it is written into the job file. " +
                    "Pass it as an environment variable instead to keep it in Nomad Variables.", name);
            }

            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    var port = ports[(name, label)];
                    job.Ports.Add(new NomadPort
                    {
                        Label = SanitizeLabel(label),
                        Static = port,
                        To = port,
                        IsExternal = binding.IsExternal,
                        IsHttp = binding.IsHttp
                    });
                }
            }

            if (resource.HasDaprSidecar)
            {
                job.DaprEnabled = true;
                job.DaprAppId = resource.DaprAppId ?? name;
                // The port from Aspire's launch profile is not what the container listens on
                // in a deployment; use the port this job actually serves HTTP on.
                job.DaprAppPort = resource.HttpBinding is { } http
                    ? ports[(name, http.Key)]
                    : resource.DaprAppPort;
                if (resource.Annotations!.TryGetValue(DaprConstants.HttpPortAnnotation, out var hp)
                    && int.TryParse(hp, out var httpP))
                {
                    job.DaprHttpPort = httpP;
                }

                if (resource.Annotations!.TryGetValue(DaprConstants.GrpcPortAnnotation, out var gp)
                    && int.TryParse(gp, out var grpcP))
                {
                    job.DaprGrpcPort = grpcP;
                }

                job.DaprVersion = config.Dapr.DaprVersion;
            }

            if (overrides?.ArtifactUrl is not null)
            {
                job.ArtifactUrl = overrides.ArtifactUrl;
            }

            jobs.Add(job);
        }

        foreach (var placeholder in resolver.Unresolved)
        {
            logger.LogWarning(
                "Could not resolve {Placeholder}; it is passed to the job unchanged. " +
                "Set the value with a serviceOverrides env entry.", placeholder);
        }

        return jobs;
    }

    /// <summary>Writes <c>{job}.nomad.json</c> to <paramref name="outputDirectory"/> and returns its path.</summary>
    public async Task<string> WriteJobAsync(
        NomadJob job, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        var filePath = await NomadJobFile.WriteAsync(BuildJob(job, config), outputDirectory, ct);
        logger.LogDebug("Written job to '{FilePath}'.", filePath);
        return filePath;
    }

    // ── Job spec ──────────────────────────────────────────────────────────────

    internal static NomadApiJob BuildJob(NomadJob job, EnvironmentConfig config)
    {
        var nc = ReadConfig(config);
        var rawExec = nc.DeploymentMode == NomadDeploymentMode.RawExec;
        var driver = rawExec ? "raw_exec"
            : config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";

        var tasks = new List<NomadTask> { MainTask(job, config, nc, rawExec, driver) };

        // Dapr sidecar (container runtimes only).
        if (job.DaprEnabled && !rawExec)
        {
            tasks.Add(DaprSidecar.Task(job.Name, job.DaprAppId, job.DaprAppPort, job.DaprHttpPort, job.DaprGrpcPort,
                job.DaprVersion, config.Dapr, driver, hostNetwork: job.UseHostNetwork));
        }

        return new NomadApiJob
        {
            Id = job.Name,
            Name = job.Name,
            Namespace = nc.Namespace,
            Datacenters = [.. nc.Datacenters],
            Meta = new Dictionary<string, string>(StringComparer.Ordinal) { [NomadClient.ManagedMetaKey] = "true" },
            TaskGroups =
            [
                new NomadTaskGroup
                {
                    Name = job.Name,
                    Count = job.Count,
                    Update = UpdateStrategy(nc.AutoRevert),
                    Networks = job.Ports.Count > 0
                        ?
                        [
                            new NomadNetwork
                            {
                                Mode = "host",
                                ReservedPorts = [.. job.Ports.Select(p => new NomadNetworkPort { Label = p.Label, Value = p.Static ?? 0 })]
                            }
                        ]
                        : null,
                    // Registered at group level so the check addresses the host port directly.
                    Services = nc.RegisterServices && job.Ports.Count > 0 ? [Service(job, nc)] : null,
                    Tasks = tasks
                }
            ]
        };
    }

    /// <summary>The rolling update every Deployify job uses.</summary>
    public static NomadUpdateStrategy UpdateStrategy(bool autoRevert) => new()
    {
        MinHealthyTime = NomadDuration.Parse("30s"),
        HealthyDeadline = NomadDuration.Parse("10m"),
        ProgressDeadline = NomadDuration.Parse("15m"),
        AutoRevert = autoRevert
    };

    private static NomadService Service(NomadJob job, NomadTargetConfig nc)
    {
        var servicePort = job.Ports.FirstOrDefault(p => p.IsHttp) ?? job.Ports[0];
        var checkType = !servicePort.IsHttp ? "tcp" : nc.HealthCheckType.ToLowerInvariant();
        return new NomadService
        {
            Name = job.Name,
            PortLabel = servicePort.Label,
            Provider = nc.ServiceProvider,
            Checks = checkType == "none"
                ? null
                :
                [
                    new NomadServiceCheck
                    {
                        Type = checkType == "http" ? "http" : "tcp",
                        Path = checkType == "http" ? nc.HealthCheckPath : null,
                        Interval = NomadDuration.Parse(nc.HealthCheckInterval),
                        Timeout = NomadDuration.Parse(nc.HealthCheckTimeout),
                        CheckRestart = CheckRestart()
                    }
                ]
        };
    }

    /// <summary>Restart a task after three failed checks, with 30s grace after it starts.</summary>
    public static NomadCheckRestart CheckRestart() => new() { Limit = 3, Grace = NomadDuration.Parse("30s") };

    private static NomadTask MainTask(NomadJob job, EnvironmentConfig config, NomadTargetConfig nc, bool rawExec, string driver)
    {
        var taskConfig = new Dictionary<string, object>(StringComparer.Ordinal);
        if (rawExec)
        {
            taskConfig["command"] = job.ArtifactBinaryPath ?? $"local/{job.Name}";
            if (job.Args.Count > 0)
            {
                taskConfig["args"] = job.Args.ToList();
            }
        }
        else
        {
            taskConfig["image"] = ImageReference.Format(job.Image!, job.ImageTag);
            if (job.UseHostNetwork)
            {
                taskConfig["network_mode"] = "host";
            }

            if (job.Args.Count > 0)
            {
                taskConfig["args"] = job.Args.ToList();
            }

            if (RegistryAuth(job.Image!, nc.RegistryUsername, nc.RegistryPassword, nc.RegistryServer, config.ContainerRegistry) is { } auth)
            {
                taskConfig["auth"] = auth;
            }

            // With host networking the container shares the host's ports, so Docker port
            // mappings are neither needed nor allowed.
            if (job.Ports.Count > 0 && !job.UseHostNetwork)
            {
                taskConfig["ports"] = job.Ports.Select(p => p.Label).ToList();
            }
        }

        return new NomadTask
        {
            Name = job.Name,
            Driver = driver,
            Artifacts = rawExec && job.ArtifactUrl is not null
                ?
                [
                    new NomadTaskArtifact
                    {
                        GetterSource = job.ArtifactUrl,
                        RelativeDest = "local/",
                        // go-getter expects the archive format here; "true" is not a format and
                        // leaves the zip packed, so the binary is never found.
                        GetterOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["archive"] = "zip" }
                    }
                ]
                : null,
            Config = taskConfig,
            Env = job.Env.Count > 0 ? new Dictionary<string, string>(job.Env, StringComparer.Ordinal) : null,
            Templates = job.SecretEnv.Count > 0 || job.DiscoveredEnv.Count > 0
                ?
                [
                    ServiceDiscovery.Template(job.Name, job.DiscoveredEnv,
                        job.SecretEnv.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
                        nc.ServiceProvider)
                ]
                : null,
            Resources = new NomadResources { Cpu = nc.DefaultCpu, MemoryMB = nc.DefaultMemory }
        };
    }

    /// <summary>
    /// The Docker/Podman <c>auth</c> block, or null when no credentials are configured or the
    /// image is on a public registry. The password is a placeholder (see <see cref="NomadJobFile"/>).
    /// </summary>
    public static List<Dictionary<string, string>>? RegistryAuth(
        string image, string? username, string? passwordSource, string? server, string? containerRegistry)
    {
        var password = Secrets.Resolve(passwordSource, "DEPLOYIFY_REGISTRY_PASSWORD");
        if (username is null || password is null || IsPublicRegistry(image))
        {
            return null;
        }

        // A block in driver config is a list of objects in the API, as Nomad's own HCL parser writes it.
        return
        [
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = username,
                ["password"] = NomadJobFile.RegistryPasswordPlaceholder,
                ["server_address"] = server ?? DeriveServerAddress(image, containerRegistry)
            }
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static NomadTargetConfig ReadConfig(EnvironmentConfig config)
        => config.TargetConfig.TryGetValue("nomad", out var raw) && raw is JsonElement el
            ? el.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default) ?? new NomadTargetConfig()
            : new NomadTargetConfig();

    /// <summary>Nomad port labels may only contain letters, digits and underscores.</summary>
    private static string SanitizeLabel(string label)
        => new(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static IEnumerable<string> ReadArgs(AspireResource resource)
        => resource.ExtensionData is not null &&
            resource.ExtensionData.TryGetValue("args", out var args) &&
            args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!)
            : [];

    /// <summary>
    /// True for well-known public registries that don't need credentials. Sending Docker
    /// Hub credentials to them can make the pull fail.
    /// </summary>
    internal static bool IsPublicRegistry(string image)
        => image.StartsWith("mcr.microsoft.com/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("gcr.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("quay.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("daprio/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("redis:", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the registry server address Nomad matches credentials against.
    /// Images without a registry host are on Docker Hub.
    /// </summary>
    internal static string DeriveServerAddress(string image, string? containerRegistry)
    {
        var firstSegment = (containerRegistry ?? image).Split('/')[0];
        if (firstSegment.Contains('.') || firstSegment.Contains(':'))
        {
            if (firstSegment.Equals("docker.io", StringComparison.OrdinalIgnoreCase))
            {
                return "https://index.docker.io/v1/";
            }

            return firstSegment.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? firstSegment : $"https://{firstSegment}";
        }

        return "https://index.docker.io/v1/";
    }
}
