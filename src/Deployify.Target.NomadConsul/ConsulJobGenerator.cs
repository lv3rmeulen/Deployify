using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Target.Nomad;
using Deployify.Target.Nomad.Api;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Converts an Aspire manifest into Nomad job files (the job API's JSON) that use Consul Connect
/// service mesh for all inter-service communication.
///
/// Key conventions:
/// - Every job uses a bridge network (required by Connect).
/// - Each service registers one port with Consul (its HTTP binding when it has one).
/// - Dependencies become upstreams of the Connect sidecar service, each bound to
///   an incrementing loopback port, and Aspire placeholders such as
///   <c>{api.bindings.http.url}</c> resolve to <c>http://localhost:&lt;upstream port&gt;</c>.
/// - Upstream addresses are also injected as <c>NOMAD_UPSTREAM_ADDR_&lt;NAME&gt;</c>.
/// - When <see cref="NomadConsulTargetConfig.EnableIngressGateway"/> is true, one extra
///   job runs a Consul ingress gateway with a listener per exposed HTTP service.
/// </summary>
public sealed class ConsulJobGenerator(ILogger<ConsulJobGenerator> logger)
{
    /// <summary>Name of the synthetic ingress gateway job.</summary>
    /// <summary>Default name of the synthetic ingress gateway job (see <see cref="NomadConsulTargetConfig.IngressGatewayName"/>).</summary>
    public const string IngressJobName = "ingress-gateway";

    // ── Model generation ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ConsulJob>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        NomadConsulTargetConfig consulCfg, CancellationToken ct = default)
    {
        var ports = ServicePorts.Assign(manifest, consulCfg.BasePort);
        var jobs = new List<ConsulJob>();
        var unresolved = new HashSet<string>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var job = new ConsulJob
            {
                Name = name,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Count = overrides?.Count ?? 1,
                Dependencies = resource.References?.Select(r => r.Name)
                    .Where(d => manifest.Resources.TryGetValue(d, out var dep) && dep.IsDeployable)
                    .ToList() ?? [],
                IsProjectService = resource.IsProject,
                ExposeViaIngressGateway = consulCfg.EnableIngressGateway &&
                    resource.Bindings?.Values.Any(b => b is { IsHttp: true, IsExternal: true }) == true
            };

            job.Image = ServiceImage.For(name, resource, config);

            // Ports: the service's HTTP binding first, since that's the one Consul registers.
            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings.OrderBy(b => b.Value.IsHttp ? 0 : 1))
                {
                    job.Ports.Add(new ConsulPort
                    {
                        Label = SanitizeLabel(label),
                        ContainerPort = ports[(name, label)],
                        Protocol = binding.IsHttp ? "http" : binding.Protocol ?? "tcp",
                        IsHttp = binding.IsHttp
                    });
                }
            }

            // Upstreams: one loopback listener per dependency.
            var localPort = consulCfg.UpstreamBasePort;
            foreach (var dep in job.Dependencies)
            {
                job.Upstreams.Add(new ConsulUpstream { DestinationName = dep, LocalBindPort = localPort });
                job.Env[$"NOMAD_UPSTREAM_ADDR_{dep.ToUpperInvariant().Replace('-', '_')}"] = $"localhost:{localPort}";
                localPort++;
            }

            // Placeholders: our own bindings are local; dependencies go through their upstream.
            var upstreamPorts = job.Upstreams.ToDictionary(u => u.DestinationName, u => u.LocalBindPort);
            var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy
            {
                Host = _ => "localhost",
                Port = (res, _, containerPort) =>
                    res != name && upstreamPorts.TryGetValue(res, out var up) ? up : containerPort,
                // Envoy hands the upstream to the app as plain HTTP on loopback; mTLS is
                // handled by the mesh, so an https placeholder still becomes http://.
                Url = (res, binding, scheme, containerPort) =>
                    res != name && upstreamPorts.TryGetValue(res, out var up)
                        ? $"{(scheme is "http" or "https" ? "http" : scheme)}://localhost:{up}"
                        : null
            }, ParameterResolution.For(config));

            // Secret values go to Nomad Variables; everything else stays in the job's env block.
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            foreach (var (k, v) in env.Plain)
            {
                job.Env[k] = v;
            }

            foreach (var (k, v) in env.Secrets)
            {
                job.Env.Remove(k);
                job.SecretEnv[k] = v;
            }

            var argsHaveSecret = false;
            foreach (var arg in ReadArgs(resource))
            {
                var resolved = await resolver.Expressions.ResolveAsync(arg, ct);
                job.Args.Add(resolved.ToString());
                argsHaveSecret |= resolved.IsSecret;
            }

            if (argsHaveSecret)
            {
                logger.LogWarning(
                    "An argument of {Service} contains a secret parameter; it is written into the job file. " +
                    "Pass it as an environment variable instead to keep it in Nomad Variables.", name);
            }

            unresolved.UnionWith(resolver.Unresolved);

            if (resource.HasDaprSidecar)
            {
                job.DaprEnabled = true;
                job.DaprAppId = resource.DaprAppId ?? name;
                job.DaprAppPort = job.Ports.FirstOrDefault(p => p.IsHttp)?.ContainerPort ?? resource.DaprAppPort;
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

            jobs.Add(job);
        }

        // One ingress gateway job for every exposed service.
        var exposed = jobs.Where(jj => jj is { ExposeViaIngressGateway: true, Ports.Count: > 0 }).ToList();
        if (exposed.Count > 0)
        {
            jobs.Add(new ConsulJob
            {
                Name = string.IsNullOrWhiteSpace(consulCfg.IngressGatewayName)
                    ? IngressJobName : consulCfg.IngressGatewayName,
                IsIngressGateway = true,
                IngressListeners =
                [
                    .. exposed
                        .Select(j => new IngressListener { ServiceName = j.Name, Port = j.Ports[0].ContainerPort })
                ]
            });
        }

        foreach (var placeholder in unresolved)
        {
            logger.LogWarning(
                "Could not resolve {Placeholder}; it is passed to the job unchanged. " +
                "Set the value with a serviceOverrides env entry.", placeholder);
        }

        return jobs;
    }

    /// <summary>Writes <c>{job}.nomad.json</c> to <paramref name="outputDirectory"/> and returns its path.</summary>
    public async Task<string> WriteJobAsync(
        ConsulJob job,
        EnvironmentConfig config,
        NomadConsulTargetConfig consulCfg,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var filePath = await NomadJobFile.WriteAsync(BuildJob(job, config, consulCfg), outputDirectory, ct);
        job.JobFilePath = filePath;
        logger.LogDebug("Written Consul Connect job to '{FilePath}'.", filePath);
        return filePath;
    }

    // ── Job spec ──────────────────────────────────────────────────────────────

    internal static NomadApiJob BuildJob(ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg)
    {
        var driver = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";
        return new NomadApiJob
        {
            Id = job.Name,
            Name = job.Name,
            Namespace = cfg.NomadNamespace,
            Datacenters = [.. cfg.Datacenters],
            Meta = new Dictionary<string, string>(StringComparer.Ordinal) { [NomadClient.ManagedMetaKey] = "true" },
            TaskGroups = [job.IsIngressGateway ? IngressGatewayGroup(job) : ServiceGroup(job, config, cfg, driver)]
        };
    }

    private static NomadTaskGroup ServiceGroup(
        ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg, string driver)
    {
        var tasks = new List<NomadTask> { MainTask(job, config, cfg, driver) };

        // Dapr sidecar task (bridge network: reaches the app on localhost).
        if (job.DaprEnabled)
        {
            tasks.Add(DaprSidecar.Task(job.Name, job.DaprAppId, job.DaprAppPort, job.DaprHttpPort, job.DaprGrpcPort,
                job.DaprVersion, config.Dapr, driver, hostNetwork: false));
        }

        return new NomadTaskGroup
        {
            Name = job.Name,
            Count = job.Count,
            Update = JobGenerator.UpdateStrategy(cfg.AutoRevert, RolloutStrategies.For(config, job.Name), job.Count),
            // Bridge mode is required for Connect.
            Networks =
            [
                new NomadNetwork
                {
                    Mode = "bridge",
                    DynamicPorts = job.Ports.Count > 0
                        ? [.. job.Ports.Select(p => new NomadNetworkPort { Label = p.Label, To = p.ContainerPort })]
                        : null
                }
            ],
            // Consul service + Connect sidecar (one per service).
            Services = job.Ports.Count > 0 || job.Upstreams.Count > 0
                ? [Service(job, cfg, RolloutStrategies.For(config, job.Name).IsProgressive)]
                : null,
            Tasks = tasks
        };
    }

    private static NomadService Service(ConsulJob job, NomadConsulTargetConfig cfg, bool canaryTags = false)
    {
        NomadServiceCheck? check = null;
        if (cfg.RegisterServices && job.Ports.Count > 0)
        {
            var servicePort = job.Ports[0];
            var checkType = !servicePort.IsHttp ? "tcp" : cfg.HealthCheckType.ToLowerInvariant();
            if (checkType != "none")
            {
                var http = checkType == "http";
                check = new NomadServiceCheck
                {
                    Type = http ? "http" : "tcp",
                    Path = http ? cfg.HealthCheckPath : null,
                    // An HTTP check is routed through the sidecar so it works in bridge mode.
                    Expose = http,
                    PortLabel = http ? null : servicePort.Label,
                    Interval = NomadDuration.Parse(cfg.HealthCheckInterval),
                    Timeout = NomadDuration.Parse(cfg.HealthCheckTimeout),
                    CheckRestart = JobGenerator.CheckRestart()
                };
            }
        }

        return new NomadService
        {
            Name = job.Name,
            // Connect proxies to the container port, so register the number itself.
            PortLabel = job.Ports.Count > 0 ? job.Ports[0].ContainerPort.ToString(CultureInfo.InvariantCulture) : null,
            Checks = check is null ? null : [check],
            CanaryTags = canaryTags ? [JobGenerator.CanaryTag] : null,
            Connect = new NomadConsulConnect
            {
                SidecarService = new NomadSidecarService
                {
                    Proxy = job.Upstreams.Count > 0
                        ? new NomadConsulProxy
                        {
                            Upstreams =
                            [
                                .. job.Upstreams.Select(u => new NomadConsulUpstream
                                {
                                    DestinationName = u.DestinationName,
                                    LocalBindPort = u.LocalBindPort
                                })
                            ]
                        }
                        : null
                }
            }
        };
    }

    private static NomadTask MainTask(ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg, string driver)
    {
        var taskConfig = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["image"] = ImageReference.Format(job.Image!, job.ImageTag)
        };
        if (job.Args.Count > 0)
        {
            taskConfig["args"] = job.Args.ToList();
        }

        if (JobGenerator.RegistryAuth(job.Image!, cfg.RegistryUsername, cfg.RegistryPassword, cfg.RegistryServer,
                config.ContainerRegistry) is { } auth)
        {
            taskConfig["auth"] = auth;
        }

        if (job.Ports.Count > 0)
        {
            taskConfig["ports"] = job.Ports.Select(p => p.Label).ToList();
        }

        return new NomadTask
        {
            Name = job.Name,
            Driver = driver,
            Config = taskConfig,
            Env = job.Env.Count > 0 ? new Dictionary<string, string>(job.Env, StringComparer.Ordinal) : null,
            Templates = job.SecretEnv.Count > 0 ? [NomadVariables.Template(job.Name, job.SecretEnv.Keys)] : null,
            Resources = new NomadResources { Cpu = cfg.DefaultCpu, MemoryMB = cfg.DefaultMemory }
        };
    }

    /// <summary>
    /// The ingress gateway group: one static host port and one TCP listener per exposed
    /// service. TCP listeners work without extra Consul service-defaults entries.
    /// </summary>
    private static NomadTaskGroup IngressGatewayGroup(ConsulJob job) => new()
    {
        Name = job.Name,
        Count = job.Count,
        Networks =
        [
            new NomadNetwork
            {
                Mode = "bridge",
                ReservedPorts =
                [
                    .. job.IngressListeners.Select(l => new NomadNetworkPort
                    {
                        Label = $"inbound_{SanitizeLabel(l.ServiceName)}", Value = l.Port, To = l.Port
                    })
                ]
            }
        ],
        Services =
        [
            new NomadService
            {
                Name = job.Name,
                PortLabel = job.IngressListeners[0].Port.ToString(CultureInfo.InvariantCulture),
                Connect = new NomadConsulConnect
                {
                    Gateway = new NomadConsulGateway
                    {
                        Proxy = new NomadConsulGatewayProxy(),
                        Ingress = new NomadConsulIngressConfig
                        {
                            Listeners =
                            [
                                .. job.IngressListeners.Select(l => new NomadConsulIngressListener
                                {
                                    Port = l.Port,
                                    Protocol = "tcp",
                                    Services = [new NomadConsulIngressService { Name = l.ServiceName }]
                                })
                            ]
                        }
                    }
                }
            }
        ]
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeLabel(string label)
        => new(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static IEnumerable<string> ReadArgs(AspireResource resource)
        => resource.ExtensionData is not null &&
            resource.ExtensionData.TryGetValue("args", out var args) &&
            args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!)
            : [];
}
