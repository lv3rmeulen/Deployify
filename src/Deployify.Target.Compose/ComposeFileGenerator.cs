using System.Text;
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
    // Track host port allocation to avoid collisions across services
    private readonly Dictionary<int, string> _allocatedPorts = [];
    private int _nextHostPort = 8000;

    public IReadOnlyList<ComposeService> Generate(
        AspireManifest manifest,
        EnvironmentConfig config,
        ComposeTargetConfig composeConfig)
    {
        _allocatedPorts.Clear();
        _nextHostPort = 8000;

        var services = new List<ComposeService>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
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
                IsProjectService = resource.Type.StartsWith("project.v")
            };

            // Image
            if (resource.Type.StartsWith("container.v"))
            {
	            svc.Image = resource.Image;
            }
            else if (resource.Type.StartsWith("project.v"))
            {
	            svc.Image = config.ContainerRegistry is not null
		            ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
		            : name;
            }

            // Env vars
            if (resource.Env is not null)
            {
	            foreach (var (k, v) in resource.Env)
	            {
		            svc.Env[k] = v;
	            }
            }

            if (overrides?.Env is not null)
            {
	            foreach (var (k, v) in overrides.Env)
	            {
		            svc.Env[k] = v;
	            }
            }

            // Ports — allocate host ports sequentially
            if (resource.Bindings is not null)
            {
                foreach (var (_, binding) in resource.Bindings)
                {
                    var containerPort = binding.ContainerPort ?? 80;
                    var hostPort = AllocateHostPort(containerPort, name);
                    svc.Ports.Add(new ComposePort
                    {
                        HostPort = hostPort,
                        ContainerPort = containerPort,
                        Protocol = binding.Protocol?.ToLowerInvariant() == "udp" ? "udp" : "tcp"
                    });
                }
            }

            // Dependencies
            svc.DependsOn = resource.References?.Select(r => r.Name).ToList() ?? [];

            // Dapr
            if (resource.HasDaprSidecar)
            {
                svc.DaprEnabled = true;
                svc.DaprAppId = resource.DaprAppId ?? name;
                svc.DaprAppPort = resource.DaprAppPort;
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

            // Named volumes from config
            if (composeConfig.Volumes.Count > 0)
            {
	            svc.NamedVolumes.AddRange(composeConfig.Volumes.Keys.Select(v => $"{v}:/data/{v}"));
            }

            // Bind mounts from config
            if (composeConfig.BindMounts.TryGetValue(name, out var mounts))
            {
	            svc.BindMounts.AddRange(mounts);
            }

            // Profiles
            if (composeConfig.GenerateProfiles)
            {
                if (svc.IsProjectService)
                {
	                svc.Profiles = ["prod"];
                }
                else
                {
	                svc.Profiles = ["dev", "prod"];
                }
            }

            services.Add(svc);
        }

        return services;
    }

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
        await File.WriteAllTextAsync(path, yaml, Encoding.UTF8, ct);
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
        await File.WriteAllTextAsync(path, yaml, Encoding.UTF8, ct);
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
        AppendService(sb, svc, cfg, indent: 2);

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
            AppendService(sb, svc, cfg, indent: 2);
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
        StringBuilder sb, ComposeService svc, ComposeTargetConfig cfg, int indent)
    {
        var pad = new string(' ', indent);
        var pad2 = new string(' ', indent + 2);
        var pad3 = new string(' ', indent + 4);

        sb.AppendLine($"{pad}{svc.Name}:");
        sb.AppendLine($"{pad2}image: {svc.Image}:{svc.ImageTag}");
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
                sb.AppendLine($"{pad3}- ./{svc.Name}/.env");
            }
            else
            {
                sb.AppendLine($"{pad2}environment:");
                foreach (var (k, v) in svc.Env)
                {
	                sb.AppendLine($"{pad3}{k}: {QuoteYaml(v)}");
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

        // Dependencies
        if (svc.DependsOn.Count > 0)
        {
            sb.AppendLine($"{pad2}depends_on:");
            foreach (var dep in svc.DependsOn)
            {
                sb.AppendLine($"{pad3}{dep}:");
                sb.AppendLine($"{new string(' ', indent + 6)}condition: service_healthy");
            }
        }

        // Health check — HTTP services get an httpGet-style check, others a TCP check
        var httpPort = svc.Ports.FirstOrDefault(p => p.IsHttp);
        if (httpPort is not null)
        {
            sb.AppendLine($"{pad2}healthcheck:");
            sb.AppendLine($"{pad3}test: [\"CMD\", \"wget\", \"-qO-\", \"http://localhost:{httpPort.ContainerPort}/health\"]");
            sb.AppendLine($"{pad3}interval: 15s");
            sb.AppendLine($"{pad3}timeout: 5s");
            sb.AppendLine($"{pad3}retries: 3");
            sb.AppendLine($"{pad3}start_period: 10s");
        }
        else if (svc.Ports.Count > 0)
        {
            var firstPort = svc.Ports[0];
            sb.AppendLine($"{pad2}healthcheck:");
            sb.AppendLine($"{pad3}test: [\"CMD\", \"nc\", \"-z\", \"localhost\", \"{firstPort.ContainerPort}\"]");
            sb.AppendLine($"{pad3}interval: 15s");
            sb.AppendLine($"{pad3}timeout: 5s");
            sb.AppendLine($"{pad3}retries: 3");
            sb.AppendLine($"{pad3}start_period: 10s");
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

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int AllocateHostPort(int containerPort, string serviceName)
    {
        // Prefer the container port itself if it's free and above 1024
        if (containerPort > 1024 && _allocatedPorts.TryAdd(containerPort, serviceName))
        {
	        return containerPort;
        }

        // Otherwise find the next free port above 8000
        while (_allocatedPorts.ContainsKey(_nextHostPort))
        {
	        _nextHostPort++;
        }

        _allocatedPorts[_nextHostPort] = serviceName;
        return _nextHostPort++;
    }

    private static string QuoteYaml(string value) =>
        value.Contains(':') || value.Contains('#') || value.StartsWith('{')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    private static string EscapeEnvValue(string value) =>
        value.Contains(' ') || value.Contains('"') || value.Contains('\'')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

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
