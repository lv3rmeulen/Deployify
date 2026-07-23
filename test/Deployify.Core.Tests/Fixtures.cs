using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Core.Tests;

internal static class Fixtures
{
    public static AspireManifest SimpleManifest() => new()
	{
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { ContainerPort = 8080, Protocol = "http" },
                    ["https"] = new() { ContainerPort = 8443, Protocol = "https" }
                }
            },
            ["worker"] = new()
            {
                Type = "project.v0",
                Path = "Worker/Worker.csproj",
                References = [new AspireReference { Name = "api" }]
            },
            ["redis"] = new()
            {
                Type = "container.v0",
                Image = "redis:7",
                Bindings = new Dictionary<string, AspireBinding> { ["tcp"] = new() { ContainerPort = 6379, Protocol = "tcp" } }
            }
        }
	};

	public static AspireManifest SingleService(
		string name = "myapp", string type = "project.v0", int port = 8080) =>
		new()
		{
			Resources = new Dictionary<string, AspireResource>
			{
				[name] = new()
				{
					Type = type,
					Image = type.StartsWith("container") ? "nginx:latest" : null,
					Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = port, Protocol = "http" } }
				}
			}
		};

	public static AspireManifest ManifestWithIgnoredTypes() =>
		new()
		{
			Resources = new Dictionary<string, AspireResource>
			{
				["param1"] = new() { Type = "parameter.v0" },
				["val1"] = new() { Type = "value.v0" },
				["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" }
			}
		};


	public static AspireManifest EmptyManifest() =>
		new()
		{
			Resources = new Dictionary<string, AspireResource>
			{
				["cfg"] = new() { Type = "parameter.v0" }
			}
		};

	public static EnvironmentConfig DefaultConfig(
		DeploymentTargetType target = DeploymentTargetType.Nomad,
		string registry = "registry.example.com/myapp",
		string tag = "v1.0.0") =>
		new()
		{
			Target = target,
			ContainerRegistry = registry,
			ImageTag = tag,
			OutputPath = "deployify-output"
		};

	public static EnvironmentConfig WithTargetConfig(EnvironmentConfig config, string key, object cfg)
	{
		var el = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(cfg));
		config.TargetConfig[key] = el;
		return config;
	}

	public static EnvironmentConfig NomadConfig(
		string address = "http://localhost:4646", string ns = "default", string dc = "dc1")
		=> WithTargetConfig(DefaultConfig(DeploymentTargetType.Nomad), "nomad", new
		{
			address, @namespace = ns, datacenters = new[] { dc }, deploymentMode = "Docker"
		});

	public static EnvironmentConfig KubernetesConfig(
		string prefix = "test", string? domain = null, string? context = null)
		=> WithTargetConfig(DefaultConfig(DeploymentTargetType.Kubernetes), "kubernetes", new
		{
			namespacePrefix = prefix, ingressBaseDomain = domain, context,
			generateIngress = domain is not null, defaultReplicas = 1,
			cpuRequest = "100m", cpuLimit = "500m",
			memoryRequest = "128Mi", memoryLimit = "512Mi", ingressClassName = "nginx"
		});

	public static EnvironmentConfig ComposeConfig(
		string project = "testproject", string restart = "unless-stopped",
		bool profiles = false, bool envFile = false)
		=> WithTargetConfig(DefaultConfig(DeploymentTargetType.Compose), "compose", new
		{
			projectName = project, restartPolicy = restart,
			generateProfiles = profiles, generateEnvFile = envFile,
			generateMergedFile = true, generateHealthChecks = true
		});
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployify-tests-" + System.IO.Path.GetRandomFileName());
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
}
