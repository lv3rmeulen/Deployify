using System.Text;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.NomadConsul;

/// <summary>
/// Converts an Aspire manifest into Nomad HCL job files that use Consul Connect
/// service mesh for all inter-service communication.
///
/// Key conventions:
/// - Every job uses <c>network { mode = "bridge" }</c>(required by Connect).
/// - Ports are NOT exposed on the host. Instead the container port is registered
///   with the Consul sidecar via <c>port</c> labels inside <c>network {}</c> and
///   the <c>service { port = ... }</c> reference.
/// - Dependencies are rendered as <c>upstreams { ... }</c> inside
///   <c>sidecar_service {}</c>, each bound to an incrementing loopback port.
/// - Upstream loopback ports are injected as env vars so the application can
///   discover its dependencies at runtime without hardcoded port numbers:
///   e.g. <c>NOMAD_UPSTREAM_ADDR_redis = "localhost:20000"</c>.
/// - When <see cref="NomadConsulTargetConfig.EnableIngressGateway"/> is true,
///   services with HTTP bindings receive an additional
///   <c>connect { gateway { ingress { ... } } }</c> stanza.
/// </summary>
public sealed class ConsulJobGenerator(ILogger<ConsulJobGenerator> logger)
{
	// ── Model generation ──────────────────────────────────────────────────────

	public IReadOnlyList<ConsulJob> Generate(
		AspireManifest manifest,
		EnvironmentConfig config,
		NomadConsulTargetConfig consulCfg)
	{
		var jobs = new List<ConsulJob>();

		foreach (var (name, resource) in manifest.Resources)
		{
			if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
			{
				continue;
			}

			config.ServiceOverrides.TryGetValue(name, out var overrides);

			var job = new ConsulJob
			{
				Name = name,
				ImageTag = overrides?.ImageTag ?? config.ImageTag,
				Count = overrides?.Count ?? 1,
				Dependencies = resource.References?.Select(r => r.Name).ToList() ?? [],
				IsProjectService = resource.Type.StartsWith("project.v"),
				ExposeViaIngressGateway = consulCfg.EnableIngressGateway && resource.Bindings?.Values.Any(b => b.Protocol is "http" or "https") == true,
				Image = resource.Type.StartsWith("container.v")
					? resource.Image
					: config.ContainerRegistry is not null
						? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
						: name
			};

			if (resource.Env is not null)
			{
				foreach (var (k, v) in resource.Env)
				{
					job.Env[k] = v;
				}
			}

			if (overrides?.Env is not null)
			{
				foreach (var (k, v) in overrides.Env)
				{
					job.Env[k] = v;
				}
			}

			if (resource.Bindings is not null)
			{
				foreach (var (label, binding) in resource.Bindings)
				{
					job.Ports.Add(new ConsulPort
					{
						Label = label,
						ContainerPort = binding.ContainerPort ?? -1, //TODO: 0 or -1 for auto-assign? Nomad requires a port number for the service registration, but we could use a dynamic port and then register that with Consul. For now, let's assume the user provides a container port.
						Protocol = binding.Protocol ?? "tcp"
					});
				}
			}

			// Dapr
			if (resource.HasDaprSidecar)
			{
				job.DaprEnabled = true;
				job.DaprAppId = resource.DaprAppId ?? name;
				job.DaprAppPort = resource.DaprAppPort;
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

		// Resolve upstreams — assign a loopback port to each dependency.
		// We do this in a second pass so every job is known before we wire them.
		ResolveUpstreams(jobs, consulCfg);

		return jobs;
	}

	private static void ResolveUpstreams(List<ConsulJob> jobs, NomadConsulTargetConfig cfg)
	{
		// Map: service name → first port label (used as the Consul service port)
		var servicePort = jobs.ToDictionary(
			j => j.Name,
			j => j.Ports.FirstOrDefault()?.ContainerPort ?? 80);

		foreach (var job in jobs)
		{
			var localPort = cfg.UpstreamBasePort;
			foreach (var dep in job.Dependencies)
			{
				job.Upstreams.Add(new ConsulUpstream
				{
					DestinationName = dep,
					LocalBindPort = localPort
				});

				// Inject discovery env var so the app doesn't hardcode ports
				job.Env[$"NOMAD_UPSTREAM_ADDR_{dep.ToUpperInvariant().Replace('-', '_')}"] =
					$"localhost:{localPort}";

				localPort++;
			}
		}
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
		await File.WriteAllTextAsync(filePath, RenderHcl(job, config, consulCfg), Encoding.UTF8, ct);
		job.HclPath = filePath;
		logger.LogDebug("Written Consul Connect HCL to '{FilePath}'.", filePath);
		return filePath;
	}

	// ── HCL renderer ──────────────────────────────────────────────────────────

	internal static string RenderHcl(ConsulJob job, EnvironmentConfig config, NomadConsulTargetConfig cfg)
	{
		var sb = new StringBuilder();
		var dc = string.Join(", ", cfg.Datacenters.Select(d => $"\"{d}\""));
		var driver = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";

		sb.AppendLine($"job \"{job.Name}\" {{");
		sb.AppendLine($"  namespace = \"{cfg.NomadNamespace}\"");
		sb.AppendLine($"  datacenters = [{dc}]");
		sb.AppendLine("  type = \"service\"");
		sb.AppendLine();

		sb.AppendLine($"  group \"{job.Name}\" {{");
		sb.AppendLine($"    count = {job.Count}");
		sb.AppendLine();

		// ── network (bridge mode required for Connect) ─────────────────────
		sb.AppendLine("    network {");
		sb.AppendLine("      mode = \"bridge\"");
		foreach (var port in job.Ports)
		{
			sb.AppendLine($"      port \"{port.Label}\" {{ to = {port.ContainerPort} }}");
		}

		sb.AppendLine("    }");
		sb.AppendLine();

		// ── Consul service + Connect sidecar ──────────────────────────────
		if (job.Ports.Count > 0 || job.Upstreams.Count > 0)
		{
			sb.AppendLine("    service {{");
			sb.AppendLine($"      name = \"{job.Name}\"");
			if (job.Ports.Count > 0)
			{
				sb.AppendLine($"      port = \"{job.Ports[0].Label}\"");
			}

			sb.AppendLine();

			// ── Health check ──────────────────────────────────────────────
			if (cfg.RegisterServices && job.Ports.Count > 0)
			{
				sb.AppendLine("      check {");
				sb.AppendLine("        type     = \"http\"");
				sb.AppendLine($"        path     = \"{cfg.HealthCheckPath}\"");
				sb.AppendLine($"        interval = \"{cfg.HealthCheckInterval}\"");
				sb.AppendLine($"        timeout  = \"{cfg.HealthCheckTimeout}\"");
				sb.AppendLine("      }");
				sb.AppendLine();
				sb.AppendLine("      check_restart {");
				sb.AppendLine("        limit = 3");
				sb.AppendLine("        grace = \"30s\"");
				sb.AppendLine("      }");
				sb.AppendLine();
			}

			sb.AppendLine("      connect {");
			sb.AppendLine("        sidecar_service {");

			if (job.Upstreams.Count > 0)
			{
				sb.AppendLine("          proxy {");
				foreach (var up in job.Upstreams)
				{
					sb.AppendLine("            upstreams {");
					sb.AppendLine($"              destination_name = \"{up.DestinationName}\"");
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

		// ── Ingress Gateway service (optional) ────────────────────────────
		if (job is { ExposeViaIngressGateway: true, Ports.Count: > 0 })
		{
			var httpPort = job.Ports.FirstOrDefault(p =>
				p.Protocol is "http" or "https") ?? job.Ports[0];

			sb.AppendLine("    service {{");
			sb.AppendLine($"      name = \"{cfg.IngressGatewayName}\"");
			sb.AppendLine();
			sb.AppendLine("      connect {");
			sb.AppendLine("        gateway {");
			sb.AppendLine("          ingress {");
			sb.AppendLine("            listener {{");
			sb.AppendLine($"              port = {httpPort.ContainerPort}");
			sb.AppendLine($"              protocol = \"{httpPort.Protocol}\"");
			sb.AppendLine("              services {{");
			sb.AppendLine($"                name = \"{job.Name}\"");
			sb.AppendLine("              }");
			sb.AppendLine("            }");
			sb.AppendLine("          }");
			sb.AppendLine("        }");
			sb.AppendLine("      }");
			sb.AppendLine("    }");
			sb.AppendLine();
		}

		// ── task ──────────────────────────────────────────────────────────
		sb.AppendLine($"    task \"{job.Name}\" {{");
		sb.AppendLine($"      driver = \"{driver}\"");
		sb.AppendLine();
		sb.AppendLine("      config {");
		sb.AppendLine($"        image = \"{FormatImage(job.Image!, job.ImageTag)}\"");
		if (cfg.RegistryUsername is not null && cfg.RegistryPassword is not null && !IsPublicRegistry(job.Image!))
		{
			var serverAddress = cfg.RegistryServer ?? DeriveServerAddress(job.Image!, config.ContainerRegistry);
			sb.AppendLine("        auth {");
			sb.AppendLine($"          username       = \"{cfg.RegistryUsername}\"");
			sb.AppendLine($"          password       = \"{cfg.RegistryPassword}\"");
			sb.AppendLine($"          server_address = \"{serverAddress}\"");
			sb.AppendLine("        }");
		}

		if (job.Ports.Count > 0)
		{
			sb.AppendLine("        ports = [" +
			              string.Join(", ", job.Ports.Select(p => $"\"{p.Label}\"")) + "]");
		}

		sb.AppendLine("      }");

		if (job.Env.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("      env {");
			foreach (var (k, v) in job.Env)
			{
				sb.AppendLine($"        {k} = \"{v.Replace("\"", "\\\"")}\"");
			}

			sb.AppendLine("      }");
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
			sb.AppendLine();
			sb.AppendLine($"    task \"{job.Name}-dapr\" {{");
			sb.AppendLine($"      driver = \"{driver}\"");
			sb.AppendLine("      config {");
			sb.AppendLine($"        image = \"{DaprConstants.SidecarImage}:{job.DaprVersion}\"");
			sb.AppendLine("        args  = [");
			sb.AppendLine("          \"./daprd\",");
			sb.AppendLine($"          \"--app-id\", \"{job.DaprAppId ?? job.Name}\",");
			sb.AppendLine($"          \"--dapr-http-port\", \"{job.DaprHttpPort}\",");
			sb.AppendLine($"          \"--dapr-grpc-port\", \"{job.DaprGrpcPort}\",");
			sb.Append($"          \"--log-level\", \"{config.Dapr.LogLevel}\"");
			if (job.DaprAppPort.HasValue)
			{
				sb.AppendLine($",{Environment.NewLine}          \"--app-port\", \"{job.DaprAppPort}\"");
			}
			else
			{
				sb.AppendLine();
			}

			sb.AppendLine("        ]");
			sb.AppendLine("      }");
			sb.AppendLine("      resources {");
			sb.AppendLine("        cpu    = 50");
			sb.AppendLine("        memory = 64");
			sb.AppendLine("      }");
			sb.AppendLine("      lifecycle {");
			sb.AppendLine("        hook    = \"prestart\"");
			sb.AppendLine("        sidecar = true");
			sb.AppendLine("      }");
			sb.AppendLine("    }");
		}

		sb.AppendLine("  }");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static string FormatImage(string image, string? tag) =>
		image.Contains(':') ? image : $"{image}:{tag}";

	private static bool IsPublicRegistry(string image) =>
		image.StartsWith("mcr.microsoft.com/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("gcr.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("quay.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("daprio/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("redis:", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

	private static string DeriveServerAddress(string image, string? containerRegistry)
	{
		var source = containerRegistry ?? image;
		var firstSegment = source.Split('/')[0];
		if (firstSegment.Contains('.') || firstSegment.Contains(':'))
		{
			return firstSegment.StartsWith("http") ? firstSegment : $"https://{firstSegment}";
		}

		return "https://index.docker.io/v1/";
	}
}
