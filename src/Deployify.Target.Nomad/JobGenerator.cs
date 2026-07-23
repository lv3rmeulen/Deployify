using System.Text;
using System.Text.Json;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>Generates Nomad HCL job files from an <see cref="AspireManifest"/>.</summary>
public sealed class JobGenerator(ILogger<JobGenerator> logger)
{
	// ── Helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Returns "{image}:{tag}" unless <paramref name="image"/> already contains a colon
	/// (i.e. already has a tag like "mcr.microsoft.com/dotnet/aspire-dashboard:latest"),
	/// in which case the image is returned as-is to avoid double-tagging.
	/// </summary>
	private static string FormatImage(string image, string? tag) =>
		image.Contains(':') ? image : $"{image}:{tag}";

	/// <summary>
	/// Returns true for well-known public registries that don't require authentication.
	/// Prevents emitting an auth block with Docker Hub credentials for Microsoft or
	/// other public images, which would cause the pull to fail.
	/// </summary>
	private static bool IsPublicRegistry(string image) =>
		image.StartsWith("mcr.microsoft.com/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("ghcr.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("gcr.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("quay.io/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("daprio/", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("redis:", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("postgres:", StringComparison.OrdinalIgnoreCase) ||
		image.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Derives the Docker registry server address from the image name or container registry.
	/// Nomad uses this to match credentials to the correct registry.
	/// Docker Hub images (no hostname) use "https://index.docker.io/v1/".
	/// </summary>
	private static string DeriveServerAddress(string image, string? containerRegistry)
	{
		// Check if image or registry has an explicit hostname
		var source = containerRegistry ?? image;
		var firstSegment = source.Split('/')[0];

		// A hostname contains a dot or colon (e.g. myregistry.azurecr.io, localhost:5000)
		if (firstSegment.Contains('.') || firstSegment.Contains(':'))
		{
			return firstSegment.StartsWith("http") ? firstSegment : $"https://{firstSegment}";
		}

		// Default to Docker Hub
		return "https://index.docker.io/v1/";
	}

	/// <summary>
	/// Assigns a static port to every (resource, binding) pair that has no containerPort.
	/// Returns a dictionary keyed by (resourceName, bindingLabel) → port number.
	/// </summary>
	private static Dictionary<(string, string), int> AssignPorts(AspireManifest manifest)
	{
		var result = new Dictionary<(string, string), int>();
		var next = 8080;

		foreach (var (name, resource) in manifest.Resources)
		{
			if (resource.Bindings is null)
			{
				continue;
			}

			foreach (var (label, binding) in resource.Bindings)
			{
				if (binding.ContainerPort.HasValue)
				{
					result[(name, label)] = binding.ContainerPort.Value;
				}
				else if (label == "https" && result.TryGetValue((name, "http"), out var httpPort))
				{
					result[(name, label)] = httpPort + 1;
				}
				else
				{
					result[(name, label)] = next;
					next += 10;
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Resolves Aspire binding expression placeholders in env var values.
	/// e.g. "{weatherbff.bindings.http.targetPort}" → "8080"
	///      "{weatherservice.bindings.http.url}"    → "http://localhost:8090"
	/// </summary>
	private static string ResolveEnvValue(
		string value,
		Dictionary<(string, string), int> portAssignments)
	{
		if (!value.StartsWith('{') || !value.EndsWith('}'))
		{
			return value;
		}

		var inner = value[1..^1];
		var parts = inner.Split('.');
		if (parts.Length < 4 || parts[1] != "bindings")
		{
			return value;
		}

		var resourceName = parts[0];
		var bindingLabel = parts[2];
		var property = parts[3];

		if (!portAssignments.TryGetValue((resourceName, bindingLabel), out var port))
		{
			return value;
		}

		var scheme = bindingLabel == "https" ? "https" : "http";

		return property switch
		{
			"targetPort" => port.ToString(),
			"port" => port.ToString(),
			"url" => $"{scheme}://localhost:{port}",
			"host" => "localhost",
			_ => value
		};
	}

	public IReadOnlyList<object> GenerateJobs(AspireManifest manifest, EnvironmentConfig config)
		=> Generate(manifest, config).Cast<object>().ToList();

	public async Task<string> WriteHclAsync(
		object jobObj, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
		=> await WriteHclAsync((NomadJob)jobObj, config, outputDirectory, ct);

	// ── Strongly-typed helpers used by NomadDeploymentTarget ─────────────────

	public IReadOnlyList<NomadJob> Generate(AspireManifest manifest, EnvironmentConfig config)
	{
		var jobs = new List<NomadJob>();

		// ── Pass 1: assign static ports to all services ───────────────────────
		// Aspire bindings often have no containerPort — assign incrementing ports
		// starting from a base port so every service gets a fixed, unique port.
		var portAssignments = AssignPorts(manifest);

		foreach (var (name, resource) in manifest.Resources)
		{
			if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
			{
				continue;
			}

			config.ServiceOverrides.TryGetValue(name, out var overrides);

			var job = new NomadJob
			{
				Name = name,
				ImageTag = overrides?.ImageTag ?? config.ImageTag,
				Count = overrides?.Count ?? 1,
				Dependencies = resource.References?.Select(r => r.Name).ToList() ?? []
			};

			if (resource.Type.StartsWith("container.v"))
			{
				job.Image = resource.Image;
			}
			else if (resource.Type.StartsWith("project.v"))
			{
				job.Image = config.ContainerRegistry is not null
					? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
					: name;
			}

			if (resource.Env is not null)
			{
				foreach (var (k, v) in resource.Env)
				{
					job.Env[k] = ResolveEnvValue(v, portAssignments);
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
					var assigned = portAssignments.TryGetValue((name, label), out var p) ? (int?)p : null;
					var port = binding.ContainerPort ?? assigned;
					job.Ports.Add(new NomadPort
					{
						Label = label,
						To = port,
						Static = port,
						IsExternal = binding.IsExternal
					});
				}

				if (resource.Bindings.Values.Any(b => b.IsExternal))
				{
					job.UseHostNetwork = true;
				}
			}

			// Dapr sidecar
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

			// Artifact URL for raw_exec (written by deployify package)
			if (overrides?.ArtifactUrl is not null)
			{
				job.ArtifactUrl = overrides.ArtifactUrl;
			}

			jobs.Add(job);
		}

		return jobs;
	}

	public async Task<string> WriteHclAsync(
		NomadJob job, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
	{
		Directory.CreateDirectory(outputDirectory);
		var filePath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
		await File.WriteAllTextAsync(filePath, RenderHcl(job, config), Encoding.UTF8, ct);
		logger.LogDebug("Written HCL to '{FilePath}'.", filePath);
		return filePath;
	}

	// ── HCL renderer ──────────────────────────────────────────────────────────

	private static string RenderHcl(NomadJob job, EnvironmentConfig config)
	{
		var ns = "default";
		var dcs = new List<string> { "dc1" };
		var deploymentMode = NomadDeploymentMode.Docker;
		var driver = config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";
		string? registryUsername = null;
		string? registryPassword = null;
		string? registryServer = null;
		var registerServices = true;
		var serviceProvider = "nomad";
		var healthCheckPath = "/health";
		var healthCheckInterval = "10s";
		var healthCheckTimeout = "2s";

		if (config.TargetConfig.TryGetValue("nomad", out var raw) && raw is JsonElement el)
		{
			var nc = el.Deserialize<NomadTargetConfig>(TargetConfigJsonOptions.Default);
			if (nc is not null)
			{
				ns = nc.Namespace;
				dcs = nc.Datacenters;
				deploymentMode = nc.DeploymentMode;
				registryUsername = nc.RegistryUsername;
				registryPassword = nc.RegistryPassword;
				registryServer = nc.RegistryServer;
				registerServices = nc.RegisterServices;
				serviceProvider = nc.ServiceProvider;
				healthCheckPath = nc.HealthCheckPath;
				healthCheckInterval = nc.HealthCheckInterval;
				healthCheckTimeout = nc.HealthCheckTimeout;
			}
		}

		// RawExec overrides the driver regardless of container runtime setting
		if (deploymentMode == NomadDeploymentMode.RawExec)
		{
			driver = "raw_exec";
		}

		var sb = new StringBuilder();
		var dc = string.Join(", ", dcs.Select(d => $"\"{d}\""));

		sb.AppendLine($"job \"{job.Name}\" {{");
		sb.AppendLine($"  namespace = \"{ns}\"");
		sb.AppendLine($"  datacenters = [{dc}]");
		sb.AppendLine("  type = \"service\"");
		sb.AppendLine();
		sb.AppendLine($"  group \"{job.Name}\" {{");
		sb.AppendLine($"    count = {job.Count}");
		sb.AppendLine();
		sb.AppendLine("    update {");
		sb.AppendLine("      min_healthy_time  = \"30s\"");
		sb.AppendLine("      healthy_deadline  = \"10m\"");
		sb.AppendLine("      progress_deadline = \"15m\"");
		sb.AppendLine("    }");
		sb.AppendLine();
		sb.AppendLine("    network {");
		if (job.UseHostNetwork)
		{
			sb.AppendLine("      mode = \"host\"");
		}

		foreach (var port in job.Ports)
		{
			if (port is { Static: not null, To: not null })
			{
				sb.AppendLine($"      port \"{port.Label}\" {{");
				sb.AppendLine($"        static = {port.Static}");
				sb.AppendLine($"        to     = {port.To}");
				sb.AppendLine("      }");
			}
			else if (port.To.HasValue)
			{
				sb.AppendLine($"      port \"{port.Label}\" {{ to = {port.To} }}");
			}
			else
			{
				sb.AppendLine($"      port \"{port.Label}\" {{}}");
			}
		}

		sb.AppendLine("    }");
		sb.AppendLine();
		sb.AppendLine($"    task \"{job.Name}\" {{");
		sb.AppendLine($"      driver = \"{driver}\"");

		if (deploymentMode == NomadDeploymentMode.RawExec && job.ArtifactUrl is not null)
		{
			sb.AppendLine();
			sb.AppendLine("      artifact {");
			sb.AppendLine($"        source      = \"{job.ArtifactUrl}\"");
			sb.AppendLine("        destination = \"local/\"");
			sb.AppendLine("        options {");
			sb.AppendLine("          archive = true");
			sb.AppendLine("        }");
			sb.AppendLine("      }");
			sb.AppendLine();
		}

		sb.AppendLine("      config {");
		if (deploymentMode == NomadDeploymentMode.RawExec)
		{
			var binaryPath = job.ArtifactBinaryPath ?? $"local/{job.Name}";
			sb.AppendLine($"        command = \"{binaryPath}\"");
		}
		else
		{
			sb.AppendLine($"        image = \"{FormatImage(job.Image!, job.ImageTag)}\"");
			if (job.UseHostNetwork)
			{
				sb.AppendLine("        network_mode = \"host\"");
			}

			if (registryUsername is not null && registryPassword is not null
			                                 && !IsPublicRegistry(job.Image!))
			{
				// Derive the registry server address from the image or container registry config.
				// Nomad needs server_address to match credentials to the right registry.
				// For Docker Hub images (no hostname prefix), use "https://index.docker.io/v1/".
				var serverAddress = registryServer ?? DeriveServerAddress(job.Image!, config.ContainerRegistry);
				sb.AppendLine("        auth {");
				sb.AppendLine($"          username       = \"{registryUsername}\"");
				sb.AppendLine($"          password       = \"{registryPassword}\"");
				sb.AppendLine($"          server_address = \"{serverAddress}\"");
				sb.AppendLine("        }");
			}

			if (job.Ports.Count > 0 && !job.UseHostNetwork)
			{
				sb.AppendLine("        ports = [" +
				              string.Join(", ", job.Ports.Select(p => $"\"{p.Label}\"")) + "]");
			}
		}

		sb.AppendLine("      }");

		if (job.Env.Count > 0)
		{
			sb.AppendLine("      env {");
			foreach (var (k, v) in job.Env)
			{
				sb.AppendLine($"        {k} = \"{v.Replace("\"", "\\\"")}\"");
			}

			sb.AppendLine("      }");
		}

		sb.AppendLine("      resources {");
		sb.AppendLine("        cpu = 256");
		sb.AppendLine("        memory = 256");
		sb.AppendLine("      }");

		// ── Service registration ──────────────────────────────────────────────
		if (registerServices && job.Ports.Count > 0)
		{
			// Register one service block per port — use only http (not https) to avoid
			// duplicate health checks. If there's no http port, register the first one.
			var httpPort = job.Ports.FirstOrDefault(p => p.Label == "http") ?? job.Ports[0];
			sb.AppendLine();
			sb.AppendLine("      service {{");
			sb.AppendLine($"        name     = \"{job.Name}\"");
			sb.AppendLine($"        port     = \"{httpPort.Label}\"");
			sb.AppendLine($"        provider = \"{serviceProvider}\"");
			sb.AppendLine();
			sb.AppendLine("        check {");
			sb.AppendLine("          type     = \"http\"");
			sb.AppendLine($"          path     = \"{healthCheckPath}\"");
			sb.AppendLine($"          interval = \"{healthCheckInterval}\"");
			sb.AppendLine($"          timeout  = \"{healthCheckTimeout}\"");
			sb.AppendLine("        }");
			sb.AppendLine();
			sb.AppendLine("        check_restart {");
			sb.AppendLine("          limit = 3");
			sb.AppendLine("          grace = \"30s\"");
			sb.AppendLine("        }");
			sb.AppendLine("      }");
		}

		sb.AppendLine("    }");

		// ── Dapr sidecar task (container runtimes only — not applicable to raw_exec) ──
		if (job.DaprEnabled && deploymentMode != NomadDeploymentMode.RawExec)
		{
			var appId = job.DaprAppId ?? job.Name;
			var appPortArg = job.DaprAppPort.HasValue ? $" --app-port {job.DaprAppPort}" : string.Empty;
			var componentsArg = config.Dapr.ComponentsPath is not null
				? $" --components-path {config.Dapr.ComponentsPath}" : string.Empty;
			var configArg = config.Dapr.ConfigPath is not null
				? $" --config {config.Dapr.ConfigPath}" : string.Empty;

			sb.AppendLine();
			sb.AppendLine($"    task \"{job.Name}-dapr\" {{");
			sb.AppendLine($"      driver = \"{driver}\"");
			sb.AppendLine("      config {");
			sb.AppendLine($"        image = \"{DaprConstants.SidecarImage}:{job.DaprVersion}\"");
			sb.AppendLine("        args  = [");
			sb.AppendLine("          \"./daprd\",");
			sb.AppendLine($"          \"--app-id\", \"{appId}\",");
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

			if (config.Dapr.ComponentsPath is not null)
			{
				sb.AppendLine($"          , \"--components-path\", \"{config.Dapr.ComponentsPath}\"");
			}

			if (config.Dapr.ConfigPath is not null)
			{
				sb.AppendLine($"          , \"--config\", \"{config.Dapr.ConfigPath}\"");
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
}
