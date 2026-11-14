using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
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

    public async Task<IReadOnlyList<ComposeService>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        ComposeTargetConfig composeConfig, CancellationToken ct = default)
    {
        var services = new List<ComposeService>();
        var ports = ServicePorts.Assign(manifest, composeConfig.BasePort);

        // Services share one Compose network and reach each other by service name.
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy(), ParameterResolution.For(config));

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
                IsProjectService = resource.IsProject
            };

            svc.Image = ServiceImage.For(name, resource, config);

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            svc.Env = env.Plain;
            svc.SecretEnv = env.Secrets;

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
                // Your own code (projects and Dockerfile builds) runs from source in dev.
                svc.Profiles = resource.IsBuiltImage ? ["prod"] : ["dev", "prod"];
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

        if (composeConfig.GenerateEnvFile && HasEnv(svc))
        {
            var envPath = Path.Combine(dir, ".env");
            await WriteEnvFileAsync(svc, envPath, ct);
            svc.EnvFilePath = envPath;
        }
        else if (!composeConfig.GenerateEnvFile && svc.SecretEnv.Count > 0)
        {
            // Inline env: secrets appear in compose.yml as ${Parameters__x}; Compose reads the
            // values from the .env file next to the compose file.
            await WriteInterpolationEnvFileAsync(Path.Combine(dir, ".env"), InterpolationVariables([svc]), ct);
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

        var variables = InterpolationVariables(services);
        if (!composeConfig.GenerateEnvFile && variables.Count > 0)
        {
            await WriteInterpolationEnvFileAsync(Path.Combine(outputDirectory, ".env"), variables, ct);
        }

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
        if (HasEnv(svc))
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

                // Secret parameters become ${VAR} references, filled in from .env by Compose.
                foreach (var (k, v) in svc.SecretEnv)
                {
                    var value = v.Render(p => "${" + ResolvedEnvironment.VariableName(p) + "}", text: EscapeInterpolation);
                    sb.AppendLine($"{pad3}{QuoteYaml(k)}: {QuoteYaml(value)}");
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

        foreach (var (k, v) in svc.SecretEnv)
        {
            sb.AppendLine($"{k}={EscapeEnvValue(v.ToString())}");
        }

        await WriteSecretFileAsync(path, sb.ToString(), ct);
    }

    /// <summary>
    /// The variables that secret env values refer to as <c>${…}</c> when env is written
    /// inline, with their values. Keyed by <see cref="ResolvedEnvironment.VariableName"/>.
    /// </summary>
    public static SortedDictionary<string, string> InterpolationVariables(IEnumerable<ComposeService> services)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in services.SelectMany(s => s.SecretEnv.Values).SelectMany(v => v.Parameters))
        {
            result[ResolvedEnvironment.VariableName(part)] = part.Value;
        }

        return result;
    }

    private static async Task WriteInterpolationEnvFileAsync(
        string path, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Secret parameters for compose.yml — generated by deployify");
        sb.AppendLine("# Do not commit this file to source control.");
        sb.AppendLine();
        foreach (var (k, v) in variables)
        {
            sb.AppendLine($"{k}={EscapeEnvValue(v)}");
        }

        await WriteSecretFileAsync(path, sb.ToString(), ct);
    }

    /// <summary>Writes a file that may hold secrets, readable by the owner only on Unix.</summary>
    private static async Task WriteSecretFileAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, s_utf8NoBom, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static bool HasEnv(ComposeService svc) => svc.Env.Count > 0 || svc.SecretEnv.Count > 0;

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
