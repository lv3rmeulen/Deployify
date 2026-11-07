using System.Collections.Generic;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// Internal model representing a single ECS service + task definition,
/// derived from an Aspire resource.
/// </summary>
public sealed class EcsService
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Full image reference, e.g. 123456789.dkr.ecr.us-east-1.amazonaws.com/myapp/api:v1</summary>
    public string? Image { get; set; }

    public string ImageTag { get; set; } = "latest";

    public int DesiredCount { get; set; } = 1;

    public int Cpu { get; set; } = 256;

    public int Memory { get; set; } = 512;

    public Dictionary<string, string> Env { get; set; } = [];

    public List<EcsPortMapping> PortMappings { get; set; } = [];

    public List<string> Dependencies { get; set; } = [];

    public bool IsProjectService { get; set; }

    /// <summary>Path of the generated task definition JSON file.</summary>
    public string? TaskDefinitionPath { get; set; }

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
    public int DaprHttpPort { get; set; } = 3500;
    public int DaprGrpcPort { get; set; } = 50001;
    public string DaprVersion { get; set; } = "latest";
}

public sealed class EcsPortMapping
{
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string Name { get; set; } = string.Empty;
    public bool IsHttp => ContainerPort is 80 or 443 or 8080 or 8443;
}
