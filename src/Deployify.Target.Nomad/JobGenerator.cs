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
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>Generates Nomad HCL job files from an <see cref="AspireManifest"/>.</summary>
/// <remarks>
/// Every Docker task runs with host networking and a fixed port, so services reach each
/// other on <c>localhost:&lt;port&gt;</c>. That matches a single Nomad client; for a
/// multi-node cluster use the NomadConsul target, which routes through Consul Connect.
/// </remarks>
public sealed class JobGenerator(ILogger<JobGenerator> logger)
{
    public IReadOnlyList<object> GenerateJobs(AspireManifest manifest, EnvironmentConfig config)
        => [.. Generate(manifest, config)];

    public async Task<string> WriteHclAsync(
        object jobObj, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => await WriteHclAsync((NomadJob)jobObj, config, outputDirectory, ct);

    // ── Model generation ──────────────────────────────────────────────────────

    public IReadOnlyList<NomadJob> Generate(AspireManifest manifest, EnvironmentConfig config)
    {
        var nc = ReadConfig(config);
        var ports = ServicePorts.Assign(manifest, nc.BasePort);
        var resolver = new PlaceholderResolver(manifest, ports,
            new EndpointStrategy { Host = _ => "localhost" }, ParameterResolution.For(config));

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
                IsProject = resource.Type.StartsWith("project.", StringComparison.Ordinal),
                UseHostNetwork = nc.DeploymentMode == NomadDeploymentMode.Docker
            };

            if (resource.Type.StartsWith("project.", StringComparison.Ordinal))
            {
                job.Image = config.ContainerRegistry is not null
                    ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                    : name;
            }
            else
            {
                job.Image = resource.Image;
            }

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = resolver.ResolveEnvironment(resource.Env, overrides?.Env);
            job.Env = env.Plain;
            job.SecretEnv = env.Secrets;

            job.Args = [.. ReadArgs(resource).Select(resolver.Resolve)];
            if (ReadArgs(resource).Any(a => resolver.Expressions.Resolve(a).IsSecret))
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

    public async Task<string> WriteHclAsync(
        NomadJob job, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var filePath = Path.Combine(outputDirectory, $"{job.Name}.job.hcl");
        // No BOM: Nomad's HCL parser and many tools choke on it.
        await File.WriteAllTextAsync(filePath, RenderHcl(job, config), new UTF8Encoding(false), ct);
        logger.LogDebug("Written HCL to '{FilePath}'.", filePath);
        return filePath;
    }

    // ── HCL renderer ──────────────────────────────────────────────────────────

    internal static string RenderHcl(NomadJob job, EnvironmentConfig config)
    {
        var nc = ReadConfig(config);
        var rawExec = nc.DeploymentMode == NomadDeploymentMode.RawExec;
        var driver = rawExec ? "raw_exec"
            : config.ContainerRuntime == ContainerRuntime.Podman ? "podman" : "docker";
        var registryUsername = nc.RegistryUsername;
        var registryPassword = Secrets.Resolve(nc.RegistryPassword, "DEPLOYIFY_REGISTRY_PASSWORD");

        var sb = new StringBuilder();
        var dcs = string.Join(", ", nc.Datacenters.Select(Hcl.Quote));

        sb.AppendLine($"job {Hcl.Quote(job.Name)} {{");
        sb.AppendLine($"  namespace = {Hcl.Quote(nc.Namespace)}");
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
        sb.AppendLine("    update {");
        sb.AppendLine("      min_healthy_time = \"30s\"");
        sb.AppendLine("      healthy_deadline = \"10m\"");
        sb.AppendLine("      progress_deadline = \"15m\"");
        sb.AppendLine($"      auto_revert = {(nc.AutoRevert ? "true" : "false")}");
        sb.AppendLine("    }");

        // ── network ───────────────────────────────────────────────────────────
        if (job.Ports.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    network {");
            sb.AppendLine("      mode = \"host\"");
            foreach (var port in job.Ports)
            {
                sb.AppendLine($"      port {Hcl.Quote(port.Label)} {{ static = {port.Static} }}");
            }

            sb.AppendLine("    }");
        }

        // ── service registration ─────────────────────────────────────────────
        // Registered at group level so the check addresses the host port directly.
        if (nc.RegisterServices && job.Ports.Count > 0)
        {
            var servicePort = job.Ports.FirstOrDefault(p => p.IsHttp) ?? job.Ports[0];
            var checkType = !servicePort.IsHttp ? "tcp" : nc.HealthCheckType.ToLowerInvariant();

            sb.AppendLine();
            sb.AppendLine("    service {");
            sb.AppendLine($"      name = {Hcl.Quote(job.Name)}");
            sb.AppendLine($"      port = {Hcl.Quote(servicePort.Label)}");
            sb.AppendLine($"      provider = {Hcl.Quote(nc.ServiceProvider)}");

            if (checkType != "none")
            {
                sb.AppendLine();
                sb.AppendLine("      check {");
                sb.AppendLine($"        type = {Hcl.Quote(checkType == "http" ? "http" : "tcp")}");
                if (checkType == "http")
                {
                    sb.AppendLine($"        path = {Hcl.Quote(nc.HealthCheckPath)}");
                }

                sb.AppendLine($"        interval = {Hcl.Quote(nc.HealthCheckInterval)}");
                sb.AppendLine($"        timeout = {Hcl.Quote(nc.HealthCheckTimeout)}");
                sb.AppendLine();
                sb.AppendLine("        check_restart {");
                sb.AppendLine("          limit = 3");
                sb.AppendLine("          grace = \"30s\"");
                sb.AppendLine("        }");
                sb.AppendLine("      }");
            }

            sb.AppendLine("    }");
        }

        // ── main task ─────────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine($"    task {Hcl.Quote(job.Name)} {{");
        sb.AppendLine($"      driver = {Hcl.Quote(driver)}");

        if (rawExec && job.ArtifactUrl is not null)
        {
            sb.AppendLine();
            sb.AppendLine("      artifact {");
            sb.AppendLine($"        source = {Hcl.Quote(job.ArtifactUrl)}");
            sb.AppendLine("        destination = \"local/\"");
            sb.AppendLine("        options {");
            // go-getter expects the archive format here; "true" is not a format and
            // leaves the zip packed, so the binary is never found.
            sb.AppendLine("          archive = \"zip\"");
            sb.AppendLine("        }");
            sb.AppendLine("      }");
        }

        sb.AppendLine();
        sb.AppendLine("      config {");
        if (rawExec)
        {
            sb.AppendLine($"        command = {Hcl.Quote(job.ArtifactBinaryPath ?? $"local/{job.Name}")}");
            if (job.Args.Count > 0)
            {
                sb.AppendLine($"        args = [{string.Join(", ", job.Args.Select(Hcl.Quote))}]");
            }
        }
        else
        {
            sb.AppendLine($"        image = {Hcl.Quote(ImageReference.Format(job.Image!, job.ImageTag))}");
            if (job.UseHostNetwork)
            {
                sb.AppendLine("        network_mode = \"host\"");
            }

            if (job.Args.Count > 0)
            {
                sb.AppendLine($"        args = [{string.Join(", ", job.Args.Select(Hcl.Quote))}]");
            }

            if (registryUsername is not null && registryPassword is not null
                                             && !IsPublicRegistry(job.Image!))
            {
                var serverAddress = nc.RegistryServer
                    ?? DeriveServerAddress(job.Image!, config.ContainerRegistry);
                sb.AppendLine("        auth {");
                sb.AppendLine($"          username = {Hcl.Quote(registryUsername)}");
                sb.AppendLine($"          password = {Hcl.Quote(Hcl.RegistryPasswordPlaceholder)}");
                sb.AppendLine($"          server_address = {Hcl.Quote(serverAddress)}");
                sb.AppendLine("        }");
            }

            // With host networking the container shares the host's ports, so Docker port
            // mappings are neither needed nor allowed.
            if (job.Ports.Count > 0 && !job.UseHostNetwork)
            {
                sb.AppendLine($"        ports = [{string.Join(", ", job.Ports.Select(p => Hcl.Quote(p.Label)))}]");
            }
        }

        sb.AppendLine("      }");

        if (job.Env.Count > 0)
        {
            sb.AppendLine();
            // Map syntax so keys that aren't valid HCL identifiers still work.
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
        sb.AppendLine($"        cpu = {nc.DefaultCpu}");
        sb.AppendLine($"        memory = {nc.DefaultMemory}");
        sb.AppendLine("      }");
        sb.AppendLine("    }");

        // ── Dapr sidecar (container runtimes only) ───────────────────────────
        if (job.DaprEnabled && !rawExec)
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
            if (job.UseHostNetwork)
            {
                sb.AppendLine("        network_mode = \"host\"");
            }

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

        sb.AppendLine("  }");
        sb.AppendLine("}");

        return sb.ToString();
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
    private static bool IsPublicRegistry(string image)
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
