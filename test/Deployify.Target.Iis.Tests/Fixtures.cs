using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.Target.Iis.Tests;

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

    public static EnvironmentConfig Config(Action<IisTargetConfig>? configure = null, string env = "prod")
    {
        var iis = new IisTargetConfig { Host = "web01", ExternalHosts = { ["cache"] = "redis.internal" } };
        configure?.Invoke(iis);
        var config = new EnvironmentConfig
        {
            Name = env,
            Target = DeploymentTargetType.Iis,
            ImageTag = "v1",
            TargetConfig = { ["iis"] = JsonSerializer.SerializeToElement(iis, TargetConfigJsonOptions.Default) }
        };
        config.Parameters["db-password"] = Secret;
        return config;
    }

    public static IisDeploymentTarget Target(FakeProcessRunner fake)
        => new(NullLogger<IisDeploymentTarget>.Instance) { Processes = fake };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deployify-iis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Pretends 'deployify publish' ran for <paramref name="service"/>.</summary>
    public static string FakePackage(string outputDir, string service, string content = "build-1")
    {
        var package = IisPublisher.PackageDirectory(outputDir, service);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, $"{service}.dll"), content);
        var hash = IisPublisher.ComputePackageHash(package);
        File.WriteAllText(Path.Combine(outputDir, service, IisPublisher.PackageHashFile), hash);
        return hash;
    }

    /// <summary>The 'List' action answering with these marker entries.</summary>
    public static FakeProcessRunner Server(params object[] services)
        => new FakeProcessRunner().When(
            spec => spec.Args.Contains("List"),
            _ => new ProcessResult(0, "##deployify " + JsonSerializer.Serialize(new { services }), ""));

    public static bool IsAction(ProcessSpec spec, string action)
        => spec.Args.Count >= 2 && spec.Args[^2] == "-Action" && spec.Args[^1] == action;

    public static ProcessSpec Call(FakeProcessRunner fake, string action) => fake.Calls.Single(c => IsAction(c, action));
}
