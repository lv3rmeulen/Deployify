using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;

namespace Deployify.Target.Iis;

/// <summary>
/// Turns Aspire project resources into IIS sites. Every site is on the same server, so services
/// reach each other on <c>localhost</c> at their binding port. Containers (Redis, databases)
/// can't run in IIS: they are skipped, and references to them go to
/// <see cref="IisTargetConfig.ExternalHosts"/>.
/// </summary>
public sealed class IisSiteGenerator
{
    private static readonly UTF8Encoding s_utf8NoBom = new(false);

    /// <summary>
    /// Port settings Aspire puts on projects. IIS owns the listening address (the ASP.NET Core
    /// Module hands the app its port), so these would only make Kestrel warn or bind the wrong port.
    /// </summary>
    internal static readonly HashSet<string> IisOwnedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPNETCORE_URLS", "ASPNETCORE_PORT", "HTTP_PORTS", "HTTPS_PORTS",
        "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS"
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Deployable resources that aren't .NET projects, from the last call, with why each was skipped.</summary>
    public IReadOnlyList<(string Name, string Reason)> Skipped { get; private set; } = [];

    /// <summary>Placeholders from the last call that could not be resolved.</summary>
    public IReadOnlyList<string> Unresolved { get; private set; } = [];

    public static bool IsProject(AspireResource resource)
        => resource.IsProject;

    public static string SitePrefix(EnvironmentConfig config, IisTargetConfig iis)
        => iis.SiteNamePrefix ?? $"{config.Name ?? "deployify"}-";

    public async Task<IReadOnlyList<IisSite>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, IisTargetConfig iis, string? appHostDirectory = null,
        CancellationToken ct = default)
    {
        var ports = ServicePorts.Assign(manifest, iis.BasePort);
        var strategy = new EndpointStrategy
        {
            Host = name => iis.ExternalHosts.TryGetValue(name, out var host) && !string.IsNullOrWhiteSpace(host)
                ? host
                : manifest.Resources.TryGetValue(name, out var r) && IsProject(r) ? "localhost" : name
        };
        var resolver = new PlaceholderResolver(manifest, ports, strategy, ParameterResolution.For(config));
        var prefix = SitePrefix(config, iis);
        var sites = new List<IisSite>();
        var skipped = new List<(string, string)>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            if (!IsProject(resource))
            {
                skipped.Add((name, iis.ExternalHosts.ContainsKey(name)
                    ? $"runs outside IIS at {iis.ExternalHosts[name]}"
                    : "IIS hosts .NET projects only; run it elsewhere and set iis.externalHosts." + name));
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            foreach (var key in env.Plain.Keys.Where(IisOwnedVariables.Contains).ToList())
            {
                env.Plain.Remove(key);
            }

            foreach (var key in env.Secrets.Keys.Where(IisOwnedVariables.Contains).ToList())
            {
                env.Secrets.Remove(key);
            }

            var site = new IisSite
            {
                Service = name,
                SiteName = prefix + name,
                Environment = config.Name,
                ProjectPath = resource.Path is null ? null : IisPublisher.ResolveProjectPath(appHostDirectory, resource.Path),
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Env = new Dictionary<string, string>(env.Plain, StringComparer.Ordinal),
                SecretEnv = new Dictionary<string, ResolvedValue>(env.Secrets, StringComparer.Ordinal),
                SecretKeys = [.. env.Secrets.Keys.Order(StringComparer.Ordinal)],
                AppPoolIdentity = iis.AppPoolIdentity,
                AlwaysRunning = iis.AlwaysRunning,
                CertificateThumbprint = string.IsNullOrWhiteSpace(iis.CertificateThumbprint) ? null : iis.CertificateThumbprint.Replace(" ", ""),
                CertificateStore = iis.CertificateStore,
                KeepReleases = Math.Max(2, iis.KeepReleases), // the current one and at least one to roll back to
                DependsOn = resource.References?.Select(r => r.Name)
                    .Where(d => manifest.Resources.TryGetValue(d, out var dep) && dep.IsDeployable)
                    .ToList() ?? []
            };

            site.Bindings = Bindings(name, resource, ports, iis, site.CertificateThumbprint is not null);
            sites.Add(site);
        }

        Skipped = skipped;
        Unresolved = [.. resolver.Unresolved];
        return sites;
    }

    /// <summary>
    /// The site's bindings. Each service listens on its own http port with no host header, which
    /// is how the other services reach it. A configured host name adds port 80 (and 443 with a
    /// certificate, using SNI) for that name. Aspire's https binding needs a certificate.
    /// </summary>
    internal static List<IisBinding> Bindings(
        string name, AspireResource resource, IReadOnlyDictionary<(string, string), int> ports,
        IisTargetConfig iis, bool hasCertificate)
    {
        var bindings = new List<IisBinding>();
        foreach (var (label, binding) in resource.Bindings ?? [])
        {
            var scheme = (binding.Scheme ?? binding.Protocol ?? label).ToLowerInvariant();
            if (scheme == "https")
            {
                if (hasCertificate)
                {
                    bindings.Add(new IisBinding { Protocol = "https", Port = ports[(name, label)] });
                }
            }
            else if (scheme == "http" || binding.IsHttp)
            {
                bindings.Add(new IisBinding { Protocol = "http", Port = ports[(name, label)] });
            }
        }

        if (iis.HostNames.TryGetValue(name, out var host) && !string.IsNullOrWhiteSpace(host))
        {
            bindings.Add(new IisBinding { Protocol = "http", Port = 80, HostName = host });
            if (hasCertificate)
            {
                bindings.Add(new IisBinding { Protocol = "https", Port = 443, HostName = host });
            }
        }

        return bindings;
    }

    /// <summary>Writes {outputDir}/{service}/site.json (no secret values) and returns its path.</summary>
    public static async Task<string> WriteSiteFileAsync(IisSite site, string outputDirectory, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, site.Service);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "site.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(site, JsonOptions), s_utf8NoBom, ct);
        return path;
    }

    /// <summary>Writes the PowerShell script that talks to IIS to {outputDir}/deployify-iis.ps1.</summary>
    public static async Task<string> WriteScriptAsync(string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, IisRemote.ScriptFileName);
        await File.WriteAllTextAsync(path, IisRemote.Script, s_utf8NoBom, ct);
        return path;
    }
}
