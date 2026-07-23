using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.AwsEcs.Tests;

internal static class EcsFixtures
{
    public static EnvironmentConfig EcsConfig(
        string region = "us-east-1",
        string cluster = "test-cluster",
        int cpu = 256,
        int memory = 512) =>
        WithTargetConfig(BaseConfig(), "ecs", new EcsTargetConfig
        {
            Region = region,
            ClusterName = cluster,
            DefaultCpu = cpu,
            DefaultMemory = memory,
            DefaultDesiredCount = 1
        });

    public static EnvironmentConfig EcsConfigWithSubnets(
        string region = "us-east-1",
        string cluster = "test-cluster") =>
        WithTargetConfig(BaseConfig(), "ecs", new EcsTargetConfig
        {
            Region = region,
            ClusterName = cluster,
            SubnetIds = ["subnet-abc123", "subnet-def456"],
            SecurityGroupIds = ["sg-abc123"],
            ExecutionRoleArn = "arn:aws:iam::123456789:role/ecsTaskExecutionRole"
        });

    private static EnvironmentConfig BaseConfig() => new()
    {
	    Target = DeploymentTargetType.AwsEcs,
	    ContainerRegistry = "123456789.dkr.ecr.us-east-1.amazonaws.com/myapp",
	    ImageTag = "v1.0.0",
	    OutputPath = "deployify-output"
    };

    private static EnvironmentConfig WithTargetConfig(EnvironmentConfig config, string key, object cfg)
    {
        config.TargetConfig[key] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(cfg));
        return config;
    }

    // Reuse core manifest fixtures inline
    public static AspireManifest SimpleManifest() => new()
    {
	    Resources = new Dictionary<string, AspireResource>
	    {
		    ["api"] = new()
		    {
			    Type = "project.v0",
			    Path = "Api/Api.csproj",
			    Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
			    Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 8080, Protocol = "http" } }
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
        string name = "myapp", string type = "project.v0", int port = 8080) => new()
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

    public static AspireManifest ManifestWithIgnoredTypes() => new()
    {
	    Resources = new Dictionary<string, AspireResource>
	    {
		    ["param1"] = new() { Type = "parameter.v0" },
		    ["val1"] = new() { Type = "value.v0" },
		    ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" }
	    }
    };

    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployify-ecs-tests-" + System.IO.Path.GetRandomFileName());
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
