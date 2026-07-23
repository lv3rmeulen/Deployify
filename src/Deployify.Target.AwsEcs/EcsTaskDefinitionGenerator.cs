using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deployify.Core.Models;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// Generates AWS ECS task definition JSON files from Aspire resources.
///
/// Output per service:
///   {outputDir}/{serviceName}.taskdef.json
///
/// The JSON matches the RegisterTaskDefinition API shape so it can be
/// passed directly to `aws ecs register-task-definition --cli-input-json`.
/// </summary>
public sealed class EcsTaskDefinitionGenerator
{
    public IReadOnlyList<EcsService> Generate(
        AspireManifest manifest,
        EnvironmentConfig config,
        EcsTargetConfig ecsConfig)
    {
        var services = new List<EcsService>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (resource.Type is "value.v0" or "parameter.v0" or "dapr.v0" or "dapr.component.v0")
            {
	            continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);

            var svc = new EcsService
            {
                Name = name,
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                DesiredCount = overrides?.Count ?? ecsConfig.DefaultDesiredCount,
                Cpu = ecsConfig.DefaultCpu,
                Memory = ecsConfig.DefaultMemory,
                IsProjectService = resource.Type.StartsWith("project.v"),
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? []
            };

            // Resolve image
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

            // Port mappings
            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    svc.PortMappings.Add(new EcsPortMapping
                    {
                        Name = label,
                        ContainerPort = binding.ContainerPort ?? 80,
                        Protocol = binding.Protocol?.ToLowerInvariant() == "udp" ? "udp" : "tcp"
                    });
                }
            }

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

            services.Add(svc);
        }

        return services;
    }

    public async Task<string> WriteTaskDefinitionAsync(
        EcsService service,
        EnvironmentConfig config,
        EcsTargetConfig ecsConfig,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var taskDef = BuildTaskDefinition(service, config, ecsConfig);
        var json = JsonSerializer.Serialize(taskDef, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var path = Path.Combine(outputDirectory, $"{service.Name}.taskdef.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
        service.TaskDefinitionPath = path;
        return path;
    }

    // ── Task definition builder ───────────────────────────────────────────────

    private static object BuildTaskDefinition(
        EcsService service, EnvironmentConfig config, EcsTargetConfig ecsConfig)
    {
        var logGroup = ecsConfig.LogGroupName ?? $"/deployify/{ecsConfig.ClusterName}";
        var imageRef = $"{service.Image}:{service.ImageTag}";

        var containerDef = new Dictionary<string, object?>
        {
            ["name"] = service.Name,
            ["image"] = imageRef,
            ["essential"] = true,
            ["cpu"] = service.Cpu,
            ["memory"] = service.Memory
        };

        if (service.PortMappings.Count > 0)
        {
            containerDef["portMappings"] = service.PortMappings.Select(p => new Dictionary<string, object>
            {
                ["containerPort"] = p.ContainerPort,
                ["protocol"] = p.Protocol,
                ["name"] = p.Name
            }).ToList();
        }

        if (service.Env.Count > 0)
        {
            containerDef["environment"] = service.Env
                .Select(kvp => new Dictionary<string, string>
                {
                    ["name"] = kvp.Key,
                    ["value"] = kvp.Value
                }).ToList();
        }

        // CloudWatch logging
        containerDef["logConfiguration"] = new Dictionary<string, object>
        {
            ["logDriver"] = "awslogs",
            ["options"] = new Dictionary<string, string>
            {
                ["awslogs-group"] = logGroup,
                ["awslogs-region"] = ecsConfig.Region,
                ["awslogs-stream-prefix"] = service.Name
            }
        };

        var containerDefs = new List<Dictionary<string, object?>> { containerDef };

        if (service.DaprEnabled)
        {
            var daprArgs = new List<string>
            {
                "./daprd",
                "--app-id", service.DaprAppId ?? service.Name,
                "--dapr-http-port", service.DaprHttpPort.ToString(),
                "--dapr-grpc-port", service.DaprGrpcPort.ToString(),
                "--log-level", config.Dapr.LogLevel
            };
            if (service.DaprAppPort.HasValue)
            { daprArgs.Add("--app-port"); daprArgs.Add(service.DaprAppPort.Value.ToString()); }

            if (config.Dapr.ComponentsPath is not null)
            { daprArgs.Add("--components-path"); daprArgs.Add(config.Dapr.ComponentsPath); }

            containerDefs.Add(new Dictionary<string, object?>
            {
                ["name"] = $"{service.Name}-dapr",
                ["image"] = $"{DaprConstants.SidecarImage}:{service.DaprVersion}",
                ["essential"] = false,
                ["command"] = daprArgs,
                ["portMappings"] = new[]
                {
                    new Dictionary<string, object> { ["containerPort"] = service.DaprHttpPort, ["protocol"] = "tcp" },
                    new Dictionary<string, object> { ["containerPort"] = service.DaprGrpcPort, ["protocol"] = "tcp" }
                },
                ["dependsOn"] = new[]
                {
                    new Dictionary<string, object> { ["containerName"] = service.Name, ["condition"] = "START" }
                },
                ["cpu"] = 64,
                ["memory"] = 128
            });
        }

        var taskDef = new Dictionary<string, object?>
        {
            ["family"] = service.Name,
            ["containerDefinitions"] = containerDefs,
            ["cpu"] = service.Cpu.ToString(),
            ["memory"] = service.Memory.ToString(),
            ["networkMode"] = "awsvpc",
            ["requiresCompatibilities"] = new[] { ecsConfig.LaunchType.ToString().ToUpperInvariant() }
        };

        if (ecsConfig.ExecutionRoleArn is not null)
        {
	        taskDef["executionRoleArn"] = ecsConfig.ExecutionRoleArn;
        }

        if (ecsConfig.TaskRoleArn is not null)
        {
	        taskDef["taskRoleArn"] = ecsConfig.TaskRoleArn;
        }

        return taskDef;
    }
}
