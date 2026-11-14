using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>References to other services are looked up in the service catalog at runtime, not pinned to localhost.</summary>
public sealed class ServiceDiscoveryTests
{
    private const string ManifestJson = """
        {
          "resources": {
            "cache": {
              "type": "container.v0",
              "image": "redis:7",
              "connectionString": "{cache.bindings.tcp.host}:{cache.bindings.tcp.port}",
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 6379 } }
            },
            "api": {
              "type": "container.v0",
              "image": "api:1",
              "env": { "ConnectionStrings__cache": "{cache.connectionString}", "PLAIN": "hello" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 } }
            },
            "web": {
              "type": "container.v0",
              "image": "web:1",
              "env": { "services__api__http__0": "{api.bindings.http.url}", "LITERAL": "a{{b" },
              "args": [ "--api", "{api.bindings.http.url}" ]
            }
          }
        }
        """;

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;

    private readonly JobGenerator _sut = new(NullLogger<JobGenerator>.Instance);

    private static EnvironmentConfig Config(object? extra = null)
        => Fixtures.WithTargetConfig(Fixtures.DefaultConfig(DeploymentTargetType.Nomad), "nomad", extra ?? new
        {
            address = "http://localhost:4646", datacenters = new[] { "dc1" }, deploymentMode = "Docker"
        });

    private async Task<(NomadJob Job, string Hcl)> RenderAsync(string name, EnvironmentConfig config)
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var job = (await _sut.GenerateAsync(Manifest(), config, ct)).Single(j => j.Name == name);
        var hcl = await File.ReadAllTextAsync(await _sut.WriteHclAsync(job, config, dir.Path, ct), ct);
        return (job, hcl);
    }

    [Fact]
    public async Task Reference_MovesToTemplate_WithNomadServiceLookup()
    {
        var (job, hcl) = await RenderAsync("web", Config());

        job.Env.Keys.ShouldBe(new[] { "LITERAL" });
        job.DiscoveredEnv["services__api__http__0"].ShouldBe($"http://{ServiceDiscovery.Token("api")}:8080");

        hcl.ShouldNotContain("\"services__api__http__0\" =");
        hcl.ShouldContain("template {");
        hcl.ShouldContain("range nomadService 1 (env \\\"NOMAD_ALLOC_ID\\\") \\\"api\\\"");
        hcl.ShouldContain("services__api__http__0=http://{{ $svc0 }}:8080");
        hcl.ShouldContain("change_mode = \"restart\"");
        hcl.ShouldNotContain("nomadVar");
    }

    [Fact]
    public async Task ConnectionString_UsesLookedUpHost()
    {
        var (job, hcl) = await RenderAsync("api", Config());

        job.Env.Keys.ShouldBe(new[] { "PLAIN" });
        hcl.ShouldContain("ConnectionStrings__cache={{ $svc0 }}:6379");
        hcl.ShouldContain("\\\"cache\\\"");
    }

    [Fact]
    public async Task ConsulProvider_UsesConsulServiceLookup()
    {
        var (_, hcl) = await RenderAsync("web", Config(new
        {
            datacenters = new[] { "dc1" }, deploymentMode = "Docker", serviceProvider = "consul"
        }));

        hcl.ShouldContain("range service \\\"api\\\"");
        hcl.ShouldNotContain("nomadService");
    }

    [Fact]
    public async Task RegisterServicesOff_FallsBackToLocalhost()
    {
        var (job, hcl) = await RenderAsync("web", Config(new
        {
            datacenters = new[] { "dc1" }, deploymentMode = "Docker", registerServices = false
        }));

        job.DiscoveredEnv.ShouldBeEmpty();
        job.Env["services__api__http__0"].ShouldBe("http://localhost:8080");
        hcl.ShouldNotContain("template {");
    }

    [Fact]
    public async Task Args_CannotBeTemplated_FallBackToLocalhost()
    {
        var (job, _) = await RenderAsync("web", Config());

        job.Args.ShouldBe(new[] { "--api", "http://localhost:8080" });
    }

    [Fact]
    public void TemplateData_EscapesLiteralBracesAndOrdersOutput()
    {
        var data = ServiceDiscovery.TemplateData("web",
            new Dictionary<string, string>
            {
                ["B"] = $"x{{{{y {ServiceDiscovery.Token("b")}",
                ["A"] = $"{ServiceDiscovery.Token("a")}:1"
            },
            new Dictionary<string, string>(), "nomad");

        data.ShouldBe(
            "{{ $svc0 := \"\" }}{{ range nomadService 1 (env \"NOMAD_ALLOC_ID\") \"a\" }}{{ $svc0 = .Address }}{{ end -}}\n" +
            "{{ $svc1 := \"\" }}{{ range nomadService 1 (env \"NOMAD_ALLOC_ID\") \"b\" }}{{ $svc1 = .Address }}{{ end -}}\n" +
            "A={{ $svc0 }}:1\n" +
            "B=x{{ \"{{\" }}y {{ $svc1 }}\n");
    }

    [Fact]
    public void ForDisplay_ShowsServiceName()
    {
        ServiceDiscovery.ForDisplay($"http://{ServiceDiscovery.Token("api")}:8080")
            .ShouldBe("http://<api via service discovery>:8080");
    }
}
