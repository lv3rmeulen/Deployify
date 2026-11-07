using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Target.Compose;

/// <summary>
/// Generates Docker Compose YAML files from Aspire resources.
///
/// Output structure per service:
///   {outputDir}/{serviceName}/compose.yml   — service definition
///   {outputDir}/{serviceName}/.env          — env vars (if enabled)
///
/// Plus a merged file:
///   {outputDir}/compose.yml                 — all services combined
/// </summary>
public sealed class ComposeFileGenerator
{
    private static readonly UTF8Encoding s_utf8NoBom = new(false);

    public IReadOnlyList<ComposeService> Generate(
        AspireManifest manifest,
        EnvironmentConfig config,
        ComposeTargetConfig composeConfig)
    {
        var services = new List<ComposeService>();
        var ports = ServicePorts.Assign(manifest, composeConfig.BasePort);

        // Services share one Compose network and reach each other by service name.
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy());

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var svc = new ComposeService
            {
                Name = name,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Replicas = overrides?.Count ?? 1,
                RestartPolicy = composeConfig.RestartPolicy,
                IsProjectService = resource.Type.StartsWith("project.", StringComparison.Ordinal)
            };

            svc.Image = svc.IsProjectService
                ? config.ContainerRegistry is not null
                    ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                    : name
                : resource.Image;

            foreach (var (k, v) in resolver.ResolveAll(resource.Env))
            {
                svc.Env[k] = v;
            }

            if (overrides?.Env is not null)
            {
                foreach (var (k, v) in overrides.Env)
                {
                    svc.Env[k] = v;
                }
            }

            // Ports — each binding has a unique port, published on the same host port.
            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    var port = ports[(name, label)];
                    svc.Ports.Add(new ComposePort
                    {
                        HostPort = port,
                        ContainerPort = port,
                        Protocol = binding.Protocol?.ToLowerInvariant() == "udp" ? "udp" : "tcp",
                        IsHttp = binding.IsHttp
                    });
                }
            }

            svc.DependsOn = resource.References?.Select(r => r.Name)
                .Where(d => manifest.Resources.TryGetValue(d, out var dep) && dep.IsDeployable)
                .ToList() ?? [];

            if (resource.HasDaprSidecar)
            {
                svc.DaprEnabled = true;
                svc.DaprAppId = resource.DaprAppId ?? name;
                svc.DaprAppPort = resource.HttpBinding is { } http ? ports[(name, http.Key)] : resource.DaprAppPort;
                if (resource.Annotations!.TryGetValue(DaprConstants.HttpPortAnnotation, out var hp)
                    && int.TryParse(hp, out var httpP))
                {
                    svc.DaprHttpPort = httpP;
                }

                if (resource.Annotations!.TryGetValue(DaprConstants.GrpcPortAnnotation, out var gp)
                    && int.TryParse(gp, out var grpcP))
                {
                    svc.DaprGrpcPort = grpcP;
                }

                svc.DaprVersion = config.Dapr.DaprVersion;
            }

            if (composeConfig.Volumes.Count > 0)
            {
                svc.NamedVolumes.AddRange(composeConfig.Volumes.Keys.Select(v => $"{v}:/data/{v}"));
            }

            if (composeConfig.BindMounts.TryGetValue(name, out var mounts))
            {
                svc.BindMounts.AddRange(mounts);
            }

            if (composeConfig.GenerateProfiles)
            {
                svc.Profiles = svc.IsProjectService ? ["prod"] : ["dev", "prod"];
            }

            services.Add(svc);
        }

        Unresolved = [.. resolver.Unresolved];
        return services;
    }

    /// <summary>Placeholders from the last <see cref="Generate"/> call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    public async Task<string> WriteServiceFileAsync(
        ComposeService svc,
        string outputDirectory,
        ComposeTargetConfig composeConfig,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, svc.Name);
        Directory.CreateDirectory(dir);

        var yaml = RenderServiceFile(svc, composeConfig);
        var path = Path.Combine(dir, "compose.yml");
        await File.WriteAllTextAsync(path, yaml, s_utf8NoBom, ct);
        svc.ComposeFilePath = path;

        if (composeConfig.GenerateEnvFile && svc.Env.Count > 0)
        {
            var envPath = Path.Combine(dir, ".env");
            await WriteEnvFileAsync(svc, envPath, ct);
            svc.EnvFilePath = envPath;
        }

        return path;
    }

    public async Task<string> WriteMergedFileAsync(
        IReadOnlyList<ComposeService> services,
        string outputDirectory,
        ComposeTargetConfig composeConfig,
        CancellationToken ct = default)
    {
        var yaml = RenderMergedFile(services, composeConfig);
        var path = Path.Combine(outputDirectory, "compose.yml");
        await File.WriteAllTextAsync(path, yaml, s_utf8NoBom, ct);
        return path;
    }

    // ── YAML renderers ────────────────────────────────────────────────────────

    private static string RenderServiceFile(ComposeService svc, ComposeTargetConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Generated by deployify — service: {svc.Name}");
        sb.AppendLine("# Do not edit manually. Re-run 'deployify generate' to update.");
        sb.AppendLine();
        sb.AppendLine("services:");
        AppendService(sb, svc, cfg, indent: 2, envFileRef: "./.env");
        if (svc.DaprEnabled)
        {
            AppendDaprSidecar(sb, svc, cfg, indent: 2);
        }

        // Declare any named volumes used by this service
        var usedVolumes = svc.NamedVolumes
            .Select(v => v.Split(':')[0])
            .Where(v => cfg.Volumes.ContainsKey(v))
            .ToList();

        if (usedVolumes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("volumes:");
            foreach (var vol in usedVolumes)
            {
                var driver = cfg.Volumes[vol];
                sb.AppendLine($"  {vol}:");
                if (driver is not null)
                {
                    sb.AppendLine($"    driver: {driver}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("networks:");
        sb.AppendLine($"  {cfg.ProjectName}:");
        sb.AppendLine("    external: true");

        return sb.ToString();
    }

    private static string RenderMergedFile(
        IReadOnlyList<ComposeService> services, ComposeTargetConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by deployify — merged compose file");
        sb.AppendLine("# Apply with: deployify apply --target compose");
        sb.AppendLine();
        sb.AppendLine("services:");

        foreach (var svc in services)
        {
            AppendService(sb, svc, cfg, indent: 2, envFileRef: $"./{svc.Name}/.env");
            if (svc.DaprEnabled)
            {
                AppendDaprSidecar(sb, svc, cfg, indent: 2);
            }
        }

        // All named volumes
        if (cfg.Volumes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("volumes:");
            foreach (var (vol, driver) in cfg.Volumes)
            {
                sb.AppendLine($"  {vol}:");
                if (driver is not null)
                {
                    sb.AppendLine($"    driver: {driver}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("networks:");
        sb.AppendLine($"  {cfg.ProjectName}:");
        sb.AppendLine("    driver: bridge");

        return sb.ToString();
    }

    private static void AppendService(
        StringBuilder sb, ComposeService svc, ComposeTargetConfig cfg, int indent, string envFileRef)
    {
        var pad = new string(' ', indent);
        var pad2 = new string(' ', indent + 2);
        var pad3 = new string(' ', indent + 4);

        sb.AppendLine($"{pad}{svc.Name}:");
        sb.AppendLine($"{pad2}image: {QuoteYaml(ImageReference.Format(svc.Image!, svc.ImageTag))}");
        sb.AppendLine($"{pad2}restart: {svc.RestartPolicy}");

        // Profiles
        if (svc.Profiles.Count > 0)
        {
            sb.AppendLine($"{pad2}profiles:");
            foreach (var p in svc.Profiles)
            {
                sb.AppendLine($"{pad3}- {p}");
            }
        }

        // Ports
        if (svc.Ports.Count > 0)
        {
            sb.AppendLine($"{pad2}ports:");
            foreach (var p in svc.Ports)
            {
                sb.AppendLine($"{pad3}- \"{p.HostPort}:{p.ContainerPort}/{p.Protocol}\"");
            }
        }

        // Env — reference .env file if generated, else inline
        if (svc.Env.Count > 0)
        {
            if (cfg.GenerateEnvFile)
            {
                sb.AppendLine($"{pad2}env_file:");
                sb.AppendLine($"{pad3}- {envFileRef}");
            }
            else
            {
                sb.AppendLine($"{pad2}environment:");
                foreach (var (k, v) in svc.Env)
                {
                    sb.AppendLine($"{pad3}{QuoteYaml(k)}: {QuoteYaml(EscapeInterpolation(v))}");
                }
            }
        }

        // Named volumes
        if (svc.NamedVolumes.Count > 0 || svc.BindMounts.Count > 0)
        {
            sb.AppendLine($"{pad2}volumes:");
            foreach (var v in svc.NamedVolumes)
            {
                sb.AppendLine($"{pad3}- {v}");
            }

            foreach (var m in svc.BindMounts)
            {
                sb.AppendLine($"{pad3}- {m}");
            }
        }

        // Network
        sb.AppendLine($"{pad2}networks:");
        sb.AppendLine($"{pad3}- {cfg.ProjectName}");

        // Dependencies — wait for the container to start. (A service_healthy condition would
        // need an in-container healthcheck command, which most images can't run.)
        if (svc.DependsOn.Count > 0)
        {
            sb.AppendLine($"{pad2}depends_on:");
            foreach (var dep in svc.DependsOn)
            {
                sb.AppendLine($"{pad3}{dep}:");
                sb.AppendLine($"{new string(' ', indent + 6)}condition: service_started");
            }
        }

        sb.AppendLine();
    }

    private static async Task WriteEnvFileAsync(
        ComposeService svc, string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# .env for {svc.Name} — generated by deployify");
        sb.AppendLine("# Do not commit secrets to source control.");
        sb.AppendLine();
        foreach (var (k, v) in svc.Env)
        {
            sb.AppendLine($"{k}={EscapeEnvValue(v)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), s_utf8NoBom, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Always a double-quoted YAML scalar, so values like <c>true</c>, <c>8080</c> or an
    /// empty string stay strings. A JSON string literal is valid YAML.
    /// </summary>
    private static string QuoteYaml(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Compose interpolates <c>$</c> in YAML values; <c>$$</c> is a literal dollar.</summary>
    private static string EscapeInterpolation(string value) => value.Replace("$", "$$");

    /// <summary>
    /// Formats a .env value. Single quotes are literal in Compose (no interpolation, no
    /// escapes); values that contain a single quote use double quotes with escaping.
    /// </summary>
    private static string EscapeEnvValue(string value)
    {
        if (value.Length > 0 && !value.Contains('\'') && !value.Contains('\n'))
        {
            return $"'{value}'";
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$")
            .Replace("\n", "\\n") + "\"";
    }

    private static void AppendDaprSidecar(
        StringBuilder sb, ComposeService svc, ComposeTargetConfig cfg, int indent)
    {
        var pad = new string(' ', indent);
        var pad2 = new string(' ', indent + 2);
        var pad4 = new string(' ', indent + 4);
        var sidecarName = $"{svc.Name}-dapr";

        sb.AppendLine($"{pad}{sidecarName}:");
        sb.AppendLine($"{pad2}image: {DaprConstants.SidecarImage}:{svc.DaprVersion}");
        sb.AppendLine($"{pad2}command:");
        sb.AppendLine($"{pad4}- ./daprd");
        sb.AppendLine($"{pad4}- --app-id");
        sb.AppendLine($"{pad4}- {svc.DaprAppId ?? svc.Name}");
        sb.AppendLine($"{pad4}- --dapr-http-port");
        sb.AppendLine($"{pad4}- \"{svc.DaprHttpPort}\"");
        sb.AppendLine($"{pad4}- --dapr-grpc-port");
        sb.AppendLine($"{pad4}- \"{svc.DaprGrpcPort}\"");
        if (svc.DaprAppPort.HasValue)
        {
            sb.AppendLine($"{pad4}- --app-port");
            sb.AppendLine($"{pad4}- \"{svc.DaprAppPort}\"");
        }

        sb.AppendLine($"{pad2}network_mode: service:{svc.Name}");
        sb.AppendLine($"{pad2}depends_on:");
        sb.AppendLine($"{pad4}- {svc.Name}");
        sb.AppendLine($"{pad2}restart: {svc.RestartPolicy}");
        if (cfg.GenerateProfiles)
        {
            sb.AppendLine($"{pad2}profiles:");
            sb.AppendLine($"{pad4}- prod");
        }

        sb.AppendLine();
    }
}
