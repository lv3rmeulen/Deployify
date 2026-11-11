using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;

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
    public async Task<IReadOnlyList<EcsService>> GenerateAsync(
        AspireManifest manifest,
        EnvironmentConfig config,
        EcsTargetConfig ecsConfig, CancellationToken ct = default)
    {
        var services = new List<EcsService>();
        var ports = ServicePorts.Assign(manifest, ecsConfig.BasePort);
        // With Service Connect each service is reachable at <name>:<port>.
        var resolver = new PlaceholderResolver(manifest, ports, new EndpointStrategy(), ParameterResolution.For(config));

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
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
                IsProjectService = resource.Type.StartsWith("project.", StringComparison.Ordinal),
                Dependencies = resource.References?.Select(r => r.Name).ToList() ?? []
            };

            svc.Image = svc.IsProjectService
                ? config.ContainerRegistry is not null
                    ? $"{config.ContainerRegistry.TrimEnd('/')}/{name}"
                    : name
                : resource.Image;

            // Values that contain a secret parameter are kept apart and stored the target's own way.
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            svc.Env = env.Plain;
            svc.SecretEnv = env.Secrets;

            if (resource.Bindings is not null)
            {
                foreach (var (label, binding) in resource.Bindings)
                {
                    svc.PortMappings.Add(new EcsPortMapping
                    {
                        Name = label,
                        ContainerPort = ports[(name, label)],
                        Protocol = binding.Protocol?.ToLowerInvariant() == "udp" ? "udp" : "tcp"
                    });
                }
            }

            // Dapr
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

            services.Add(svc);
        }

        Unresolved = [.. resolver.Unresolved];
        return services;
    }

    /// <summary>Placeholders from the last <see cref="Generate"/> call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

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
        // No BOM: the AWS CLI rejects JSON input that starts with one.
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        if (config.Secrets.IsExternal && service.SecretEnv.Count > 0)
        {
            SecretLeakScanner.EnsureClean([path], SecretKeys.SecretValues(service.SecretEnv.Values));
        }

        service.TaskDefinitionPath = path;
        return path;
    }

    /// <summary>The SSM parameter that holds secret env var <paramref name="key"/> of a service.</summary>
    public static string SecretParameterName(EcsTargetConfig ecsConfig, string serviceName, string key)
        => $"{SecretsPrefix(ecsConfig)}/{serviceName}/{key}";

    /// <summary>SSM path under which generated parameter values are kept (ssm backend).</summary>
    public static string GeneratedParametersPath(EcsTargetConfig ecsConfig) => $"{SecretsPrefix(ecsConfig)}/_parameters";

    /// <summary>The SSM path prefix: <c>secretsPrefix</c>, or <c>/deployify/{cluster}</c>.</summary>
    public static string SecretsPrefix(EcsTargetConfig ecsConfig)
    {
        var prefix = string.IsNullOrWhiteSpace(ecsConfig.SecretsPrefix)
            ? $"/deployify/{ecsConfig.ClusterName}"
            : ecsConfig.SecretsPrefix.TrimEnd('/');
        if (!prefix.StartsWith('/'))
        {
            prefix = "/" + prefix;
        }

        return prefix;
    }

    // ── Task definition builder ───────────────────────────────────────────────

    private static object BuildTaskDefinition(
        EcsService service, EnvironmentConfig config, EcsTargetConfig ecsConfig)
    {
        var logGroup = ecsConfig.LogGroupName ?? $"/deployify/{ecsConfig.ClusterName}";
        var imageRef = ImageReference.Format(service.Image!, service.ImageTag);

        // CPU and memory are set on the task only. Giving the app container the full task
        // allowance left no room for the Dapr sidecar, and ECS rejects a task whose
        // containers reserve more than the task has.
        var containerDef = new Dictionary<string, object?>
        {
            ["name"] = service.Name,
            ["image"] = imageRef,
            ["essential"] = true
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

        // Secret env vars are read from SSM Parameter Store at task start; apply writes them.
        if (service.SecretEnv.Count > 0)
        {
            containerDef["secrets"] = service.SecretEnv.Keys
                .Select(key => new Dictionary<string, string>
                {
                    ["name"] = key,
                    ["valueFrom"] = SecretParameterName(ecsConfig, service.Name, key)
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
                ["awslogs-stream-prefix"] = service.Name,
                // Create the log group on first start; otherwise every task fails to
                // launch until someone creates it by hand.
                ["awslogs-create-group"] = "true"
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
            {
                daprArgs.Add("--app-port");
                daprArgs.Add(service.DaprAppPort.Value.ToString());
            }

            if (config.Dapr.ComponentsPath is not null)
            {
                daprArgs.Add("--resources-path");
                daprArgs.Add(config.Dapr.ComponentsPath);
            }

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
                ["memoryReservation"] = 64
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
