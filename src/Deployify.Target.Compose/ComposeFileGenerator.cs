using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Yaml;

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
public sealed partial class ComposeFileGenerator
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

    // ── Compose files ─────────────────────────────────────────────────────────

    private static string RenderServiceFile(ComposeService svc, ComposeTargetConfig cfg)
    {
        var services = new Dictionary<string, ComposeServiceDefinition>(StringComparer.Ordinal);
        AddService(services, svc, cfg, envFileRef: "./.env");

        // Declare the named volumes this service uses.
        var volumes = svc.NamedVolumes
            .Select(v => v.Split(':')[0])
            .Where(cfg.Volumes.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(v => v, v => Volume(cfg.Volumes[v]), StringComparer.Ordinal);

        return Render(
            [$"Generated by deployify — service: {svc.Name}", "Do not edit manually. Re-run 'deployify generate' to update."],
            services, volumes,
            new ComposeNetworkDefinition { External = true }, cfg);
    }

    private static string RenderMergedFile(IReadOnlyList<ComposeService> services, ComposeTargetConfig cfg)
    {
        var definitions = new Dictionary<string, ComposeServiceDefinition>(StringComparer.Ordinal);
        foreach (var svc in services)
        {
            AddService(definitions, svc, cfg, envFileRef: $"./{svc.Name}/.env");
        }

        return Render(
            ["Generated by deployify — merged compose file", "Apply with: deployify apply --target compose"],
            definitions,
            cfg.Volumes.ToDictionary(v => v.Key, v => Volume(v.Value), StringComparer.Ordinal),
            new ComposeNetworkDefinition { Driver = "bridge" }, cfg);
    }

    /// <summary>A header comment, then services, volumes and networks, each section separated by a blank line.</summary>
    private static string Render(
        IEnumerable<string> header,
        Dictionary<string, ComposeServiceDefinition> services,
        Dictionary<string, ComposeVolumeDefinition?> volumes,
        ComposeNetworkDefinition network,
        ComposeTargetConfig cfg)
    {
        var sb = new StringBuilder();
        foreach (var line in header)
        {
            sb.Append("# ").Append(line).Append('\n');
        }

        // A blank line between services, as people write Compose files by hand.
        var servicesYaml = YamlWriter.SnakeCase.Serialize(new ComposeFileServices { Services = services });
        sb.Append('\n').Append(ServiceStart().Replace(servicesYaml, "\n$0").Replace("services:\n\n", "services:\n"));
        if (volumes.Count > 0)
        {
            sb.Append('\n').Append(YamlWriter.SnakeCase.Serialize(new ComposeFileVolumes { Volumes = volumes }));
        }

        sb.Append('\n').Append(YamlWriter.SnakeCase.Serialize(new ComposeFileNetworks
        {
            Networks = new Dictionary<string, ComposeNetworkDefinition>(StringComparer.Ordinal) { [cfg.ProjectName] = network }
        }));
        return sb.ToString();
    }

    /// <summary>A line that starts a service: a key indented by exactly two spaces.</summary>
    [GeneratedRegex(@"(?m)^  [^ \n]")]
    private static partial Regex ServiceStart();

    private static ComposeVolumeDefinition? Volume(string? driver) => driver is null ? null : new ComposeVolumeDefinition { Driver = driver };

    private static void AddService(
        Dictionary<string, ComposeServiceDefinition> services, ComposeService svc, ComposeTargetConfig cfg, string envFileRef)
    {
        Dictionary<string, Quoted>? environment = null;
        if (HasEnv(svc) && !cfg.GenerateEnvFile)
        {
            environment = new Dictionary<string, Quoted>(StringComparer.Ordinal);
            foreach (var (k, v) in svc.Env)
            {
                environment[k] = EscapeInterpolation(v);
            }

            // Secret parameters become ${VAR} references, filled in from .env by Compose.
            foreach (var (k, v) in svc.SecretEnv)
            {
                environment[k] = v.Render(p => "${" + ResolvedEnvironment.VariableName(p) + "}", text: EscapeInterpolation);
            }
        }

        services[svc.Name] = new ComposeServiceDefinition
        {
            Image = ImageReference.Format(svc.Image!, svc.ImageTag),
            Restart = svc.RestartPolicy,
            Profiles = svc.Profiles.Count > 0 ? [.. svc.Profiles] : null,
            Ports = svc.Ports.Count > 0 ? [.. svc.Ports.Select(p => new Quoted($"{p.HostPort}:{p.ContainerPort}/{p.Protocol}"))] : null,
            // Env: the .env file when one is generated, otherwise inline.
            EnvFile = HasEnv(svc) && cfg.GenerateEnvFile ? [envFileRef] : null,
            Environment = environment,
            Volumes = svc.NamedVolumes.Count > 0 || svc.BindMounts.Count > 0 ? [.. svc.NamedVolumes, .. svc.BindMounts] : null,
            Networks = [cfg.ProjectName],
            // Wait for the container to start. (A service_healthy condition would need an
            // in-container healthcheck command, which most images can't run.)
            DependsOn = svc.DependsOn.Count > 0
                ? svc.DependsOn.ToDictionary(d => d, _ => new ComposeDependency { Condition = "service_started" }, StringComparer.Ordinal)
                : null
        };

        if (svc.DaprEnabled)
        {
            services[$"{svc.Name}-dapr"] = DaprSidecar(svc, cfg);
        }
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

    /// <summary>The Dapr sidecar shares the service's network namespace, so it reaches the app on localhost.</summary>
    private static ComposeServiceDefinition DaprSidecar(ComposeService svc, ComposeTargetConfig cfg)
    {
        var command = new List<string>
        {
            "./daprd",
            "--app-id", svc.DaprAppId ?? svc.Name,
            "--dapr-http-port", svc.DaprHttpPort.ToString(CultureInfo.InvariantCulture),
            "--dapr-grpc-port", svc.DaprGrpcPort.ToString(CultureInfo.InvariantCulture)
        };
        if (svc.DaprAppPort.HasValue)
        {
            command.AddRange(["--app-port", svc.DaprAppPort.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        return new ComposeServiceDefinition
        {
            Image = $"{DaprConstants.SidecarImage}:{svc.DaprVersion}",
            Command = command,
            Restart = svc.RestartPolicy,
            Profiles = cfg.GenerateProfiles ? ["prod"] : null,
            NetworkMode = $"service:{svc.Name}",
            DependsOn = new List<string> { svc.Name }
        };
    }
}
