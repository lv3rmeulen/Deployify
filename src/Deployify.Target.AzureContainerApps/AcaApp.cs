using System;
using System.Collections.Generic;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// Internal model representing a single Azure Container App,
/// derived from an Aspire resource.
/// </summary>
public sealed class AcaApp
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Full image reference, e.g. myregistry.azurecr.io/myapp/api:v1</summary>
    public string? Image { get; set; }

    public string ImageTag { get; set; } = "latest";

    public int MinReplicas { get; set; } = 0;
    public int MaxReplicas { get; set; } = 3;

    public double Cpu { get; set; } = 0.25;
    public string Memory { get; set; } = "0.5Gi";

    public Dictionary<string, string> Env { get; set; } = [];

    public List<AcaPort> Ports { get; set; } = [];

    public List<string> Dependencies { get; set; } = [];

    public bool IsProjectService { get; set; }

    public AcaIngressType IngressType { get; set; } = AcaIngressType.External;

    /// <summary>Path of the generated bicep/json app spec file.</summary>
    public string? SpecFilePath { get; set; }

    // ── Dapr ──────────────────────────────────────────────────────────────────
    public bool DaprEnabled { get; set; }
    public string? DaprAppId { get; set; }
    public int? DaprAppPort { get; set; }
}

public sealed class AcaPort
{
    public int ContainerPort { get; set; }
    public string Protocol { get; set; } = "http";
    public string Name { get; set; } = string.Empty;

    /// <summary>True when the port serves HTTP; set from the Aspire binding's scheme.</summary>
    public bool IsHttp
    {
        get => _isHttp ?? (Protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                           || ContainerPort is 80 or 443 or 8080 or 8443);
        set => _isHttp = value;
    }

    private bool? _isHttp;

    /// <summary>Aspire marked the binding external (reachable from outside the environment).</summary>
    public bool IsExternal { get; set; }
}
