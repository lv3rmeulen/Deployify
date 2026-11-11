using System.Collections.Generic;
using System.Text.Json.Serialization;
using Deployify.Core.Expressions;

namespace Deployify.Target.Iis;

/// <summary>One Aspire project as an IIS site: what generate writes to site.json and apply sends the server.</summary>
public sealed class IisSite
{
    /// <summary>The Aspire resource name.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>IIS site name, also used for the application pool.</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>The environment, recorded in the server-side marker so destroy only takes its own sites.</summary>
    public string? Environment { get; set; }

    /// <summary>The .csproj to publish (absolute when it could be resolved).</summary>
    public string? ProjectPath { get; set; }

    public string ImageTag { get; set; } = "latest";

    public List<IisBinding> Bindings { get; set; } = [];

    /// <summary>Environment variables without secrets, set on the site through the ASP.NET Core Module.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Variables holding a secret; their values never go into site.json.</summary>
    [JsonIgnore]
    public Dictionary<string, ResolvedValue> SecretEnv { get; set; } = [];

    /// <summary>Names of <see cref="SecretEnv"/>, so site.json shows what is set without the values.</summary>
    public List<string> SecretKeys { get; set; } = [];

    public List<string> DependsOn { get; set; } = [];

    public string AppPoolIdentity { get; set; } = "ApplicationPoolIdentity";
    public bool AlwaysRunning { get; set; } = true;
    public string? CertificateThumbprint { get; set; }
    public string CertificateStore { get; set; } = "My";
    public int KeepReleases { get; set; } = 3;
}

public sealed class IisBinding
{
    /// <summary>http or https.</summary>
    public string Protocol { get; set; } = "http";

    public int Port { get; set; }

    /// <summary>Host header; empty matches any host name.</summary>
    public string HostName { get; set; } = string.Empty;
}
