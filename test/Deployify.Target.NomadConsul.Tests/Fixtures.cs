using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Deployify.Core.Models;

namespace Deployify.Target.NomadConsul.Tests;

internal static class Fixtures
{
    // ── Configs ───────────────────────────────────────────────────────────────

    public static NomadConsulTargetConfig DefaultConsulCfg(
        string nomadAddr = "http://localhost:4646",
        string consulAddr = "http://localhost:8500",
        string ns = "default",
        string dc = "dc1",
        int upstreamBase = 20000,
        bool ingress = false) => new()
    {
        NomadAddress = nomadAddr,
        NomadNamespace = ns,
        Datacenters = [dc],
        ConsulAddress = consulAddr,
        UpstreamBasePort = upstreamBase,
        EnableIngressGateway = ingress,
        IngressGatewayName = "ingress-gateway",
        DefaultCpu = 256,
        DefaultMemory = 256
    };

    public static EnvironmentConfig EnvConfig(
        string registry = "reg.example.com/app",
        string tag = "v1.0.0",
        NomadConsulTargetConfig? consulCfg = null)
    {
        var cfg = consulCfg ?? DefaultConsulCfg();
        var env = new EnvironmentConfig
        {
            Target = DeploymentTargetType.NomadConsul,
            ContainerRegistry = registry,
            ImageTag = tag,
            OutputPath = "deployify-output",
            TargetConfig = { ["nomadconsul"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(cfg)) }
        };
        return env;
    }

    // ── Manifests ─────────────────────────────────────────────────────────────

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
        string name = "myapp", string type = "project.v0", int port = 8080,
        string protocol = "http", bool external = false) => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            [name] = new()
            {
                Type = type,
                Image = type.StartsWith("container") ? "nginx:latest" : null,
                Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = port, Protocol = protocol, IsExternal = external } }
            }
        }
    };

    public static AspireManifest ManifestWithDependencyChain() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["frontend"] = new()
            {
                Type = "project.v0",
                Path = "Frontend/Frontend.csproj",
                Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 3000, Protocol = "http" } },
                References = [new AspireReference { Name = "api" }]
            },
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Bindings = new Dictionary<string, AspireBinding> { ["http"] = new() { ContainerPort = 8080, Protocol = "http" } },
                References = [new AspireReference { Name = "redis" }, new AspireReference { Name = "db" }]
            },
            ["redis"] = new()
            {
                Type = "container.v0",
                Image = "redis:7",
                Bindings = new Dictionary<string, AspireBinding> { ["tcp"] = new() { ContainerPort = 6379, Protocol = "tcp" } }
            },
            ["db"] = new()
            {
                Type = "container.v0",
                Image = "postgres:16",
                Bindings = new() { ["tcp"] = new() { ContainerPort = 5432, Protocol = "tcp" } }
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

    // ── TempDir ───────────────────────────────────────────────────────────────

    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "deployify-nc-tests-" + System.IO.Path.GetRandomFileName());

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
