using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Target.Nomad;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Converts an Aspire manifest into Nomad HCL job files that use Consul Connect
/// service mesh for all inter-service communication.
///
/// Key conventions:
/// - Every job uses <c>network { mode = "bridge" }</c> (required by Connect).
/// - Each service registers one port with Consul (its HTTP binding when it has one).
/// - Dependencies become <c>upstreams</c> inside <c>sidecar_service</c>, each bound to
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
                IsProjectService = resource.Type.StartsWith("project.", StringComparison.Ordinal),
                ExposeViaIngressGateway = consulCfg.EnableIngressGateway &&
                    resource.Bindings?.Values.Any(b => b is { IsHttp: true, IsExternal: true }) == true
            };

            job.Image = job.IsProjectService
                ? config.ContainerRegistry is not null
                    ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                    : name
                : resource.Image;

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

    // ── HCL writing ───────────────────────────────────────────────────────────

    public async Task<string> WriteHclAsync(
        ConsulJob job,
        EnvironmentConfig config,
        NomadConsulTargetConfig consulCfg,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var filePath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
        await File.WriteAllTextAsync(filePath, RenderHcl(job, config, consulCfg), new UTF8Encoding(false), ct);
        job.HclPath = filePath;
        logger.LogDebug("Written Consul Connect HCL to '{FilePath}'.", filePath);
        return filePath;
    }

    // ── HCL renderer ──────────────────────────────────────────────────────────

    internal static string RenderHcl(ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg)
    {
        var sb = new StringBuilder();
        var dcs = string.Join(", ", cfg.Datacenters.Select(Hcl.Quote));
        var driver = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";

        sb.AppendLine($"job {Hcl.Quote(job.Name)} {{");
        sb.AppendLine($"  namespace = {Hcl.Quote(cfg.NomadNamespace)}");
        sb.AppendLine($"  datacenters = [{dcs}]");
        sb.AppendLine("  type = \"service\"");
        sb.AppendLine();
        sb.AppendLine("  meta = {");
        sb.AppendLine($"    {Hcl.Quote(NomadClient.ManagedMetaKey)} = \"true\"");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine($"  group {Hcl.Quote(job.Name)} {{");
        sb.AppendLine($"    count = {job.Count}");
        sb.AppendLine();

        if (job.IsIngressGateway)
        {
            RenderIngressGateway(sb, job);
        }
        else
        {
            RenderService(sb, job, config, cfg, driver);
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void RenderService(
        StringBuilder sb, ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg, string driver)
    {
        sb.AppendLine("    update {");
        sb.AppendLine("      min_healthy_time = \"30s\"");
        sb.AppendLine("      healthy_deadline = \"10m\"");
        sb.AppendLine("      progress_deadline = \"15m\"");
        sb.AppendLine($"      auto_revert = {(cfg.AutoRevert ? "true" : "false")}");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── network (bridge mode required for Connect) ─────────────────────
        sb.AppendLine("    network {");
        sb.AppendLine("      mode = \"bridge\"");
        foreach (var port in job.Ports)
        {
            sb.AppendLine($"      port {Hcl.Quote(port.Label)} {{ to = {port.ContainerPort} }}");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // ── Consul service + Connect sidecar (one block per service) ───────
        if (job.Ports.Count > 0 || job.Upstreams.Count > 0)
        {
            sb.AppendLine("    service {");
            sb.AppendLine($"      name = {Hcl.Quote(job.Name)}");
            if (job.Ports.Count > 0)
            {
                // Connect proxies to the container port, so register the number itself.
                sb.AppendLine($"      port = {Hcl.Quote(job.Ports[0].ContainerPort.ToString())}");
            }

            if (cfg.RegisterServices && job.Ports.Count > 0)
            {
                var servicePort = job.Ports[0];
                var checkType = !servicePort.IsHttp ? "tcp" : cfg.HealthCheckType.ToLowerInvariant();
                if (checkType != "none")
                {
                    sb.AppendLine();
                    sb.AppendLine("      check {");
                    sb.AppendLine($"        type = {Hcl.Quote(checkType == "http" ? "http" : "tcp")}");
                    if (checkType == "http")
                    {
                        sb.AppendLine($"        path = {Hcl.Quote(cfg.HealthCheckPath)}");
                        // Route the check through the sidecar so it works in bridge mode.
                        sb.AppendLine("        expose = true");
                    }
                    else
                    {
                        sb.AppendLine($"        port = {Hcl.Quote(servicePort.Label)}");
                    }

                    sb.AppendLine($"        interval = {Hcl.Quote(cfg.HealthCheckInterval)}");
                    sb.AppendLine($"        timeout = {Hcl.Quote(cfg.HealthCheckTimeout)}");
                    sb.AppendLine();
                    sb.AppendLine("        check_restart {");
                    sb.AppendLine("          limit = 3");
                    sb.AppendLine("          grace = \"30s\"");
                    sb.AppendLine("        }");
                    sb.AppendLine("      }");
                }
            }

            sb.AppendLine();
            sb.AppendLine("      connect {");
            sb.AppendLine("        sidecar_service {");
            if (job.Upstreams.Count > 0)
            {
                sb.AppendLine("          proxy {");
                foreach (var up in job.Upstreams)
                {
                    sb.AppendLine("            upstreams {");
                    sb.AppendLine($"              destination_name = {Hcl.Quote(up.DestinationName)}");
                    sb.AppendLine($"              local_bind_port = {up.LocalBindPort}");
                    sb.AppendLine("            }");
                }

                sb.AppendLine("          }");
            }

            sb.AppendLine("        }");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ── task ──────────────────────────────────────────────────────────
        sb.AppendLine($"    task {Hcl.Quote(job.Name)} {{");
        sb.AppendLine($"      driver = {Hcl.Quote(driver)}");
        sb.AppendLine();
        sb.AppendLine("      config {");
        sb.AppendLine($"        image = {Hcl.Quote(ImageReference.Format(job.Image!, job.ImageTag))}");
        if (job.Args.Count > 0)
        {
            sb.AppendLine($"        args = [{string.Join(", ", job.Args.Select(Hcl.Quote))}]");
        }

        var registryPassword = Secrets.Resolve(cfg.RegistryPassword, "DEPLOYIFY_REGISTRY_PASSWORD");
        if (cfg.RegistryUsername is not null && registryPassword is not null && !IsPublicRegistry(job.Image!))
        {
            var serverAddress = cfg.RegistryServer ?? DeriveServerAddress(job.Image!, config.ContainerRegistry);
            sb.AppendLine("        auth {");
            sb.AppendLine($"          username = {Hcl.Quote(cfg.RegistryUsername)}");
            sb.AppendLine($"          password = {Hcl.Quote(Hcl.RegistryPasswordPlaceholder)}");
            sb.AppendLine($"          server_address = {Hcl.Quote(serverAddress)}");
            sb.AppendLine("        }");
        }

        if (job.Ports.Count > 0)
        {
            sb.AppendLine($"        ports = [{string.Join(", ", job.Ports.Select(p => Hcl.Quote(p.Label)))}]");
        }

        sb.AppendLine("      }");

        if (job.Env.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("      env = {");
            foreach (var (k, v) in job.Env)
            {
                sb.AppendLine($"        {Hcl.Quote(k)} = {Hcl.Quote(v)}");
            }

            sb.AppendLine("      }");
        }

        if (job.SecretEnv.Count > 0)
        {
            NomadVariables.AppendTemplate(sb, job.Name, job.SecretEnv.Keys, "      ");
        }

        sb.AppendLine();
        sb.AppendLine("      resources {");
        sb.AppendLine($"        cpu = {cfg.DefaultCpu}");
        sb.AppendLine($"        memory = {cfg.DefaultMemory}");
        sb.AppendLine("      }");
        sb.AppendLine("    }");

        // ── Dapr sidecar task (bridge network — reaches app via localhost) ──
        if (job.DaprEnabled)
        {
            var daprArgs = new List<string>
            {
                "./daprd",
                "--app-id", job.DaprAppId ?? job.Name,
                "--dapr-http-port", job.DaprHttpPort.ToString(),
                "--dapr-grpc-port", job.DaprGrpcPort.ToString(),
                "--log-level", config.Dapr.LogLevel
            };
            if (job.DaprAppPort.HasValue)
            {
                daprArgs.AddRange(["--app-port", job.DaprAppPort.Value.ToString()]);
            }

            if (config.Dapr.ComponentsPath is not null)
            {
                daprArgs.AddRange(["--resources-path", config.Dapr.ComponentsPath]);
            }

            if (config.Dapr.ConfigPath is not null)
            {
                daprArgs.AddRange(["--config", config.Dapr.ConfigPath]);
            }

            sb.AppendLine();
            sb.AppendLine($"    task {Hcl.Quote($"{job.Name}-dapr")} {{");
            sb.AppendLine($"      driver = {Hcl.Quote(driver)}");
            sb.AppendLine("      config {");
            sb.AppendLine($"        image = {Hcl.Quote($"{DaprConstants.SidecarImage}:{job.DaprVersion}")}");
            sb.AppendLine($"        args = [{string.Join(", ", daprArgs.Select(Hcl.Quote))}]");
            sb.AppendLine("      }");
            sb.AppendLine("      resources {");
            sb.AppendLine("        cpu = 50");
            sb.AppendLine("        memory = 64");
            sb.AppendLine("      }");
            sb.AppendLine("      lifecycle {");
            sb.AppendLine("        hook = \"prestart\"");
            sb.AppendLine("        sidecar = true");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
        }
    }

    /// <summary>
    /// Renders the ingress gateway group: one static host port and one TCP listener per
    /// exposed service. TCP listeners work without extra Consul service-defaults entries.
    /// </summary>
    private static void RenderIngressGateway(StringBuilder sb, ConsulJob job)
    {
        sb.AppendLine("    network {");
        sb.AppendLine("      mode = \"bridge\"");
        foreach (var l in job.IngressListeners)
        {
            sb.AppendLine($"      port {Hcl.Quote($"inbound_{SanitizeLabel(l.ServiceName)}")} {{");
            sb.AppendLine($"        static = {l.Port}");
            sb.AppendLine($"        to = {l.Port}");
            sb.AppendLine("      }");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    service {");
        sb.AppendLine($"      name = {Hcl.Quote(job.Name)}");
        sb.AppendLine($"      port = {Hcl.Quote(job.IngressListeners[0].Port.ToString())}");
        sb.AppendLine();
        sb.AppendLine("      connect {");
        sb.AppendLine("        gateway {");
        sb.AppendLine("          proxy {}");
        sb.AppendLine();
        sb.AppendLine("          ingress {");
        foreach (var l in job.IngressListeners)
        {
            sb.AppendLine("            listener {");
            sb.AppendLine($"              port = {l.Port}");
            sb.AppendLine("              protocol = \"tcp\"");
            sb.AppendLine("              service {");
            sb.AppendLine($"                name = {Hcl.Quote(l.ServiceName)}");
            sb.AppendLine("              }");
            sb.AppendLine("            }");
        }

        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SanitizeLabel(string label)
        => new(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static IEnumerable<string> ReadArgs(AspireResource resource)
        => resource.ExtensionData is not null &&
            resource.ExtensionData.TryGetValue("args", out var args) &&
            args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!)
            : [];

    private static bool IsPublicRegistry(string image)
        => image.StartsWith("mcr.microsoft.com/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("gcr.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("quay.io/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("daprio/", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("redis:", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

    private static string DeriveServerAddress(string image, string? containerRegistry)
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
