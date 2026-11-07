using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubernetesManifestWriterTests
{
    private readonly KubernetesManifestGenerator _sut = new();

    private KubernetesTargetConfig KubernetesCfg(string prefix = "test", string? domain = null) => new()
    {
        NamespacePrefix = prefix, DefaultReplicas = 1,
        CpuRequest = "100m", CpuLimit = "500m",
        MemoryRequest = "128Mi", MemoryLimit = "512Mi",
        GenerateIngress = domain is not null, IngressBaseDomain = domain,
        IngressClassName = "nginx"
    };

    [Fact]
    public async Task Write_CreatesNamespaceYaml()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldContain(f => f.EndsWith("namespace.yaml"));
    }

    [Fact]
    public async Task Write_CreatesDeploymentYaml()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldContain(f => f.EndsWith("deployment.yaml"));
    }

    [Fact]
    public async Task Write_CreatesServiceYaml_WhenPortsPresent()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldContain(f => f.EndsWith("service.yaml"));
    }

    [Fact]
    public async Task Write_CreatesConfigMapYaml_WhenEnvVarsPresent()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), KubernetesCfg());
        var api = svcs.Single(s => s.ServiceName == "api");
        var files = await _sut.WriteYamlAsync(api, dir.Path, TestContext.Current.CancellationToken);
        files.ShouldContain(f => f.EndsWith("configmap.yaml"));
    }

    [Fact]
    public async Task Write_NoConfigMapYaml_WhenNoEnv()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("worker"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldNotContain(f => f.EndsWith("configmap.yaml"));
    }

    [Fact]
    public async Task Write_CreatesIngressYaml_WhenDomainConfigured()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api", "project.v0", 8080, external: true),
            Fixtures.KubernetesConfig(domain: "example.com"), KubernetesCfg(domain: "example.com"));
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldContain(f => f.EndsWith("ingress.yaml"));
    }

    [Fact]
    public async Task Write_NoIngressYaml_WhenNoDomain()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        files.ShouldNotContain(f => f.EndsWith("ingress.yaml"));
    }

    [Fact]
    public async Task Write_NamespaceYaml_ContainsNamespace()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig("myapp"), KubernetesCfg("myapp"));
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var nsYaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("namespace.yaml")), TestContext.Current.CancellationToken);
        nsYaml.ShouldContain("myapp-api");
    }

    [Fact]
    public async Task Write_DeploymentYaml_ContainsImage()
    {
        using var dir = new TempDir();
        var config = Fixtures.KubernetesConfig();
        config.ContainerRegistry = "reg.example.com/app";
        var svcs = _sut.Generate(Fixtures.SingleService("api"), config, KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var depYaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        depYaml.ShouldContain("reg.example.com/app/api");
    }

    [Fact]
    public async Task Write_DeploymentYaml_ContainsResourceRequests()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        yaml.ShouldContain("100m");
        yaml.ShouldContain("128Mi");
    }

    [Fact]
    public async Task Write_DeploymentYaml_ContainsLivenessProbe()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api", "project.v0", 8080), Fixtures.KubernetesConfig(), KubernetesCfg());
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        yaml.ShouldContain("livenessProbe:");
    }

    [Fact]
    public async Task Write_IngressYaml_ContainsHost()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("api", "project.v0", 8080, external: true),
            Fixtures.KubernetesConfig(domain: "example.com"), KubernetesCfg(domain: "example.com"));
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("ingress.yaml")), TestContext.Current.CancellationToken);
        yaml.ShouldContain("api.example.com");
    }

    [Fact]
    public async Task Write_CreatesSubdirectory_PerService()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SingleService("myapp"), Fixtures.KubernetesConfig(), KubernetesCfg());
        await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(dir.Path, "myapp")).ShouldBeTrue();
    }

    [Fact]
    public async Task Write_AllServices_ProducesSubdirPerService()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), KubernetesCfg());
        foreach (var svc in svcs)
        {
            await _sut.WriteYamlAsync(svc, dir.Path, TestContext.Current.CancellationToken);
        }

        Directory.GetDirectories(dir.Path).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Write_DeploymentYaml_ContainsReplicas()
    {
        using var dir = new TempDir();
        var kubernetesConfig = KubernetesCfg();
        kubernetesConfig.DefaultReplicas = 3;
        var svcs = _sut.Generate(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), kubernetesConfig);
        var files = await _sut.WriteYamlAsync(svcs[0], dir.Path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("deployment.yaml")), TestContext.Current.CancellationToken);
        yaml.ShouldContain("replicas: 3");
    }

    [Fact]
    public async Task Write_ConfigMapYaml_ContainsEnvKey()
    {
        using var dir = new TempDir();
        var svcs = _sut.Generate(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), KubernetesCfg());
        var api = svcs.Single(s => s.ServiceName == "api");
        var files = await _sut.WriteYamlAsync(api, dir.Path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(files.Single(f => f.EndsWith("configmap.yaml")), TestContext.Current.CancellationToken);
        yaml.ShouldContain("ASPNETCORE_ENVIRONMENT");
    }
}
