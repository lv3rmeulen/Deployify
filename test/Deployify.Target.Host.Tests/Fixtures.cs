using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Target.Host.Tests;

internal static class Fixtures
{
    public const string Secret = "S3cret-pw";

    /// <summary>Two web projects, a worker, Redis, and a secret parameter.</summary>
    public const string ManifestJson = """
        {
          "resources": {
            "db-password": {
              "type": "parameter.v0",
              "value": "{db-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "cache": {
              "type": "container.v0",
              "image": "redis:7",
              "connectionString": "{cache.bindings.tcp.host}:{cache.bindings.tcp.port}",
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 6379 } }
            },
            "api": {
              "type": "project.v0",
              "path": "../Api/Api.csproj",
              "env": {
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
                "HTTP_PORTS": "{api.bindings.http.targetPort}",
                "ConnectionStrings__cache": "{cache.connectionString}",
                "DB_PASSWORD": "{db-password.value}"
              },
              "bindings": {
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 5100 },
                "https": { "scheme": "https", "protocol": "tcp", "transport": "http", "targetPort": 5101 }
              }
            },
            "web": {
              "type": "project.v0",
              "path": "../Web/Web.csproj",
              "env": { "services__api__http__0": "{api.bindings.http.url}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 5200 } },
              "references": [ { "name": "api" } ]
            },
            "worker": {
              "type": "project.v0",
              "path": "../Worker/Worker.csproj"
            }
          }
        }
        """;

    public static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    public static EnvironmentConfig Config(Action<HostTargetConfig>? configure = null, string env = "prod")
    {
        var host = new HostTargetConfig
        {
            Hosts = ["web01"],
            Os = HostOs.Linux,
            ExternalHosts = { ["cache"] = "redis.internal" }
        };
        configure?.Invoke(host);
        var config = new EnvironmentConfig
        {
            Name = env,
            Target = DeploymentTargetType.Host,
            ImageTag = "v1",
            TargetConfig = { ["host"] = JsonSerializer.SerializeToElement(host, TargetConfigJsonOptions.Default) }
        };
        config.Parameters["db-password"] = Secret;
        return config;
    }

    /// <summary>A target whose hosts are <see cref="FakeHost"/>s, made on first use and kept by name.</summary>
    public static HostDeploymentTarget Target(Dictionary<string, FakeHost> hosts, FakeProcessRunner? processes = null)
        => new(NullLogger<HostDeploymentTarget>.Instance)
        {
            Processes = processes ?? new FakeProcessRunner(),
            HealthPollInterval = TimeSpan.FromMilliseconds(1),
            DriverFactory = (_, name) =>
            {
                var key = name ?? "this machine";
                if (!hosts.TryGetValue(key, out var host))
                {
                    hosts[key] = host = new FakeHost(key);
                }

                return host;
            }
        };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deployify-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Pretends 'deployify publish' ran for <paramref name="service"/>: an app called <paramref name="app"/>.</summary>
    public static string FakePackage(string outputDir, string service, string content = "build-1", string? app = null)
    {
        app ??= char.ToUpperInvariant(service[0]) + service[1..];
        var package = FolderPackage.PackageDirectory(outputDir, service);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, $"{app}.dll"), content);
        File.WriteAllText(Path.Combine(package, $"{app}.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(package, app), "#!/bin/sh\necho hello\n");
        var hash = FolderPackage.ComputePackageHash(package);
        File.WriteAllText(Path.Combine(outputDir, service, FolderPackage.PackageHashFile), hash);
        return hash;
    }
}

/// <summary>An in-memory host: what it runs, what it was asked to do, and scripted health answers.</summary>
internal sealed class FakeHost(string where) : IHostDriver
{
    public string Where { get; } = where;

    /// <summary>Service folder name → what runs there.</summary>
    public Dictionary<string, LiveService> Services { get; } = new(StringComparer.Ordinal);

    /// <summary>Every release each service had, newest last.</summary>
    public Dictionary<string, List<string>> Releases { get; } = new(StringComparer.Ordinal);

    public List<string> Log { get; } = [];

    public List<HostRelease> Applied { get; } = [];

    /// <summary>The health answer, by service folder name; healthy when unset.</summary>
    public Func<HostService, string, HealthObservation>? Health { get; set; }

    public string? Unavailable { get; set; }

    public List<string> Problems { get; } = [];

    public List<ServiceLogLine> Lines { get; } = [];

    public void Runs(string service, string name, string release, string fingerprint, string env = "prod")
    {
        Services[name] = new LiveService(service, name, "deployify-" + name, env, fingerprint, release, true);
        Releases[name] = [release];
    }

    public Task<string?> UnavailableReasonAsync(CancellationToken ct) => Task.FromResult(Unavailable);

    public Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Problems);

    public Task<IReadOnlyList<LiveService>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LiveService>>([.. Services.Values]);

    public Task<bool> ApplyAsync(HostRelease release, CancellationToken ct)
    {
        var s = release.Service;
        Log.Add($"apply {s.Name} {release.Release}");
        Applied.Add(release);
        var changed = !(Services.TryGetValue(s.Name, out var current) && current.Fingerprint == release.Fingerprint);
        Services[s.Name] = new LiveService(s.Service, s.Name, s.UnitName, s.Environment, release.Fingerprint, release.Release, true);
        if (!Releases.TryGetValue(s.Name, out var list))
        {
            Releases[s.Name] = list = [];
        }

        list.Add(release.Release);
        return Task.FromResult(changed);
    }

    public Task<bool> ActivateAsync(ServiceRef service, string release, CancellationToken ct)
    {
        Log.Add($"activate {service.Name} {release}");
        if (!Releases.TryGetValue(service.Name, out var list) || !list.Contains(release))
        {
            throw new InvalidOperationException($"Release {release} is no longer on {Where}.");
        }

        var current = Services[service.Name];
        Services[service.Name] = current with { Release = release, Fingerprint = "fp-" + release };
        return Task.FromResult(current.Release != release);
    }

    public Task<HealthObservation> HealthAsync(HostService service, string release, CancellationToken ct)
    {
        Log.Add($"health {service.Name} {release}");
        return Task.FromResult(Health?.Invoke(service, release) ?? HealthObservation.Healthy($"{Where} ok"));
    }

    public Task<IReadOnlyList<string>> DeleteAsync(ServiceRef service, CancellationToken ct)
    {
        Log.Add($"delete {service.Name}");
        Services.Remove(service.Name);
        return Task.FromResult<IReadOnlyList<string>>([$"unit {service.UnitName}"]);
    }

    public async IAsyncEnumerable<ServiceLogLine> LogsAsync(
        ServiceRef service, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        Log.Add($"logs {service.Name}");
        foreach (var line in Lines)
        {
            await Task.Yield();
            yield return line;
        }
    }
}
