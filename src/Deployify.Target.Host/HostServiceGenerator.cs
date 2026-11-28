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
using Deployify.Core.Services;

namespace Deployify.Target.Host;

/// <summary>
/// Turns Aspire project resources into operating-system services. Every service runs on every
/// host, so services reach each other on <c>localhost</c> at their port. Containers (Redis,
/// databases) can't run this way: they are skipped, and references to them go to
/// <see cref="HostTargetConfig.ExternalHosts"/>.
/// </summary>
public sealed class HostServiceGenerator
{
    private static readonly UTF8Encoding s_utf8NoBom = new(false);

    /// <summary>
    /// Port settings Aspire puts on projects. The host target sets <c>ASPNETCORE_URLS</c> itself,
    /// from the assigned ports and <see cref="HostTargetConfig.ListenAddress"/>, so these would
    /// only conflict with it.
    /// </summary>
    internal static readonly HashSet<string> OwnedVariables = new(StringComparer.OrdinalIgnoreCase)
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

    public static string Prefix(EnvironmentConfig config, HostTargetConfig host)
        => host.NamePrefix ?? $"{config.Name ?? "deployify"}-";

    /// <summary>
    /// A name systemd and the Windows service manager both accept: letters, digits, '-', '_'
    /// and '.', with anything else replaced by '-'.
    /// </summary>
    internal static string SafeName(string name)
        => new([.. name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')]);

    public async Task<IReadOnlyList<HostService>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, HostTargetConfig host, string? appHostDirectory = null,
        CancellationToken ct = default)
    {
        var ports = ServicePorts.Assign(manifest, host.BasePort);
        var strategy = new EndpointStrategy
        {
            Host = name => host.ExternalHosts.TryGetValue(name, out var external) && !string.IsNullOrWhiteSpace(external)
                ? external
                : manifest.Resources.TryGetValue(name, out var r) && r.IsProject ? "localhost" : name
        };
        var resolver = new PlaceholderResolver(manifest, ports, strategy, ParameterResolution.For(config));
        var prefix = Prefix(config, host);
        var listen = string.IsNullOrWhiteSpace(host.ListenAddress) ? "*" : host.ListenAddress.Trim();
        var services = new List<HostService>();
        var skipped = new List<(string, string)>();

        foreach (var (name, resource) in manifest.Resources)
        {
            if (!resource.IsDeployable)
            {
                continue;
            }

            if (!resource.IsProject)
            {
                skipped.Add((name, host.ExternalHosts.TryGetValue(name, out var external)
                    ? $"runs outside the host target at {external}"
                    : "the host target runs .NET projects only; run it elsewhere and set host.externalHosts." + name));
                continue;
            }

            config.ServiceOverrides.TryGetValue(name, out var overrides);
            var env = await resolver.ResolveEnvironmentAsync(resource.Env, overrides?.Env, ct);
            foreach (var key in env.Plain.Keys.Where(OwnedVariables.Contains).ToList())
            {
                env.Plain.Remove(key);
            }

            foreach (var key in env.Secrets.Keys.Where(OwnedVariables.Contains).ToList())
            {
                env.Secrets.Remove(key);
            }

            var httpPorts = HttpPorts(name, resource, ports);
            var plain = new Dictionary<string, string>(env.Plain, StringComparer.Ordinal);
            if (httpPorts.Count > 0)
            {
                plain["ASPNETCORE_URLS"] = string.Join(';', httpPorts.Select(p => $"http://{listen}:{p}"));
            }

            if (host.EffectiveOs == HostOs.Linux && !host.SelfContained && !string.IsNullOrWhiteSpace(host.DotnetRoot))
            {
                plain["DOTNET_ROOT"] = host.DotnetRoot;
            }

            var serviceName = prefix + name;
            services.Add(new HostService
            {
                Service = name,
                Name = SafeName(serviceName),
                UnitName = "deployify-" + SafeName(serviceName),
                Environment = config.Name,
                ProjectPath = resource.Path is null ? null : FolderPackage.ResolveProjectPath(appHostDirectory, resource.Path),
                ImageTag = overrides?.ImageTag ?? config.ImageTag,
                Ports = httpPorts,
                Env = plain,
                SecretEnv = new Dictionary<string, ResolvedValue>(env.Secrets, StringComparer.Ordinal),
                SecretKeys = [.. env.Secrets.Keys.Order(StringComparer.Ordinal)],
                KeepReleases = Math.Max(2, host.KeepReleases), // the current one and at least one to roll back to
                DependsOn = resource.References?.Select(r => r.Name)
                    .Where(d => manifest.Resources.TryGetValue(d, out var dep) && dep.IsDeployable)
                    .ToList() ?? []
            });
        }

        Skipped = skipped;
        Unresolved = [.. resolver.Unresolved];
        return services;
    }

    /// <summary>
    /// The ports of the project's http bindings. Aspire's https bindings are left out: the host
    /// target has no certificate to give Kestrel, so TLS belongs in a proxy in front of it.
    /// </summary>
    internal static List<int> HttpPorts(string name, AspireResource resource, IReadOnlyDictionary<(string, string), int> ports)
    {
        var result = new List<int>();
        foreach (var (label, binding) in resource.Bindings ?? [])
        {
            var scheme = (binding.Scheme ?? binding.Protocol ?? label).ToLowerInvariant();
            if (scheme != "https" && (scheme == "http" || binding.IsHttp) && ports.TryGetValue((name, label), out var port) &&
                !result.Contains(port))
            {
                result.Add(port);
            }
        }

        return result;
    }

    /// <summary>Writes {outputDir}/{service}/service.json (no secret values) and returns its path.</summary>
    public static async Task<string> WriteServiceFileAsync(HostService service, string outputDirectory, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, service.Service);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "service.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(service, JsonOptions), s_utf8NoBom, ct);
        return path;
    }

    /// <summary>Writes {outputDir}/{service}/{unit}.service, the systemd unit apply installs, and returns its path.</summary>
    public static async Task<string> WriteUnitFileAsync(HostService service, string root, string outputDirectory, CancellationToken ct = default)
    {
        var dir = Path.Combine(outputDirectory, service.Service);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, service.UnitName + ".service");
        await File.WriteAllTextAsync(path, SystemdUnit.Render(service, root, user: "%DEPLOY_USER%"), s_utf8NoBom, ct);
        return path;
    }

    /// <summary>Writes the scripts that talk to the hosts to the output folder (for reference) and returns their paths.</summary>
    public static async Task<IReadOnlyList<string>> WriteScriptsAsync(HostOs os, string outputDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var (fileName, content) = os == HostOs.Windows
            ? (WindowsHost.ScriptFileName, WindowsHost.Script)
            : (LinuxHost.ScriptFileName, LinuxHost.Script);
        var path = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n"), s_utf8NoBom, ct);
        return [path];
    }
}
