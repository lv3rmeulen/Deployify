using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Target.Kubernetes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Helm.Tests;

/// <summary>One chart per service: the Kubernetes target's manifests, with the overridable fields in values.yaml.</summary>
public sealed class HelmChartWriterTests
{
    private static HelmDeploymentTarget Target(FakeProcessRunner? fake = null)
        => new(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, fake ?? new FakeProcessRunner());

    private static async Task<TempDir> GenerateAsync(EnvironmentConfig? config = null, AspireManifest? manifest = null)
    {
        var dir = new TempDir();
        await Target().GenerateAsync(manifest ?? Fixtures.Manifest(), config ?? Fixtures.Config(), dir.Path,
            TestContext.Current.CancellationToken);
        return dir;
    }

    [Fact]
    public async Task Generate_WritesAChartPerService_WithoutTheNamespace()
    {
        using var dir = await GenerateAsync();

        foreach (var service in new[] { "api", "worker", "redis" })
        {
            File.Exists(dir.File("helm", service, "Chart.yaml")).ShouldBeTrue();
            File.Exists(dir.File("helm", service, "values.yaml")).ShouldBeTrue();
            File.Exists(dir.File("helm", service, "templates", "deployment.yaml")).ShouldBeTrue();
            // Helm keeps its release record in the namespace, so it has to exist before the chart.
            File.Exists(dir.File("helm", service, "templates", "namespace.yaml")).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task TheChart_KeepsThePodsImagePullSecrets()
    {
        var config = Fixtures.Config();
        config.TargetConfig["kubernetes"] = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            namespacePrefix = "test",
            imagePullSecrets = new[] { "ghcr" },
            registryCredentials = new { username = "robot", password = "env:REG_PW" }
        });
        using var dir = await GenerateAsync(config);

        var template = File.ReadAllText(dir.File("helm", "api", "templates", "deployment.yaml"));

        template.ShouldContain("imagePullSecrets:");
        template.ShouldContain("- name: \"ghcr\"");
        template.ShouldContain("- name: \"deployify-registry\"");
        template.ShouldNotContain("robot");
    }

    [Fact]
    public async Task Generate_ContactsNothing()
    {
        var fake = new FakeProcessRunner();
        using var dir = new TempDir();

        await Target(fake).GenerateAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, TestContext.Current.CancellationToken);

        fake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChartYaml_NamesTheService_AndCarriesTheImageTagAsAppVersion()
    {
        using var dir = await GenerateAsync(Fixtures.Config(helm: new { chartVersion = "1.2.3" }));

        var chart = dir.Read("helm", "api", "Chart.yaml");
        chart.ShouldContain("apiVersion: v2");
        chart.ShouldContain("name: api");
        chart.ShouldContain("version: \"1.2.3\"");
        chart.ShouldContain("appVersion: \"v1.0.0\"");
    }

    [Fact]
    public async Task Values_HoldTheImageReplicasAndResources()
    {
        using var dir = await GenerateAsync();

        var values = dir.Read("helm", "api", "values.yaml");
        values.ShouldContain("replicaCount: 2");
        values.ShouldContain("repository: \"registry.example.com/myapp/api\"");
        values.ShouldContain("tag: \"v1.0.0\"");
        values.ShouldContain("cpu: \"100m\"");
        values.ShouldContain("memory: \"512Mi\"");
    }

    [Fact]
    public async Task Deployment_ReadsTheOverridableFieldsFromValues()
    {
        using var dir = await GenerateAsync();

        var deployment = dir.Read("helm", "api", "templates", "deployment.yaml");
        deployment.ShouldContain("replicas: {{ .Values.replicaCount }}");
        deployment.ShouldContain("image: \"{{ if .Values.image.tag }}");
        deployment.ShouldContain("{{- toYaml .Values.resources | nindent 12 }}");
        // Nothing hard-coded is left behind next to the templated fields.
        deployment.ShouldNotContain("registry.example.com/myapp/api");
        deployment.ShouldNotContain("cpu: 100m");
        // The rest is the Kubernetes target's manifest, unchanged.
        deployment.ShouldContain("namespace: test-api");
        deployment.ShouldContain("app.kubernetes.io/managed-by: deployify");
    }

    [Fact]
    public async Task APinnedContainerImage_IsSplitIntoRepositoryAndTag()
    {
        using var dir = await GenerateAsync();

        var values = dir.Read("helm", "redis", "values.yaml");
        values.ShouldContain("repository: \"redis\"");
        values.ShouldContain("tag: \"7\"");
    }

    [Theory]
    [InlineData("registry.example.com/myapp/api:v1", "registry.example.com/myapp/api", "v1")]
    [InlineData("localhost:5000/api:2.0", "localhost:5000/api", "2.0")]
    [InlineData("localhost:5000/api", "localhost:5000/api", "")]
    [InlineData("redis@sha256:abc123", "redis@sha256:abc123", "")]
    [InlineData("redis", "redis", "")]
    public void SplitImage_SplitsAtTheTagOnly(string image, string repository, string tag)
        => HelmChartWriter.SplitImage(image).ShouldBe((repository, tag));

    [Fact]
    public async Task Ingress_TakesHostAndClassFromValues()
    {
        using var dir = await GenerateAsync(Fixtures.Config(domain: "example.com"));

        var ingress = dir.Read("helm", "api", "templates", "ingress.yaml");
        ingress.ShouldContain("host: {{ .Values.ingress.host | quote }}");
        ingress.ShouldContain("ingressClassName: {{ .Values.ingress.className | quote }}");
        var values = dir.Read("helm", "api", "values.yaml");
        values.ShouldContain("host: \"api.example.com\"");
        values.ShouldContain("className: \"nginx\"");
    }

    [Fact]
    public async Task WithoutAnIngress_ValuesHaveNoIngressSection()
    {
        using var dir = await GenerateAsync();

        dir.Read("helm", "api", "values.yaml").ShouldNotContain("ingress:");
        File.Exists(dir.File("helm", "api", "templates", "ingress.yaml")).ShouldBeFalse();
    }

    [Fact]
    public void LiteralTemplateBraces_AreEscapedForHelm()
    {
        // The Vault agent annotation is itself a Go template; Helm must print it, not run it.
        var escaped = HelmChartWriter.EscapeTemplateActions("{{ with secret \"kv/app\" }}{{ .Data.data.pw }}{{ end }}");

        escaped.ShouldBe(
            "{{ \"{{\" }} with secret \"kv/app\" {{ \"}}\" }}{{ \"{{\" }} .Data.data.pw {{ \"}}\" }}{{ \"{{\" }} end {{ \"}}\" }}");
    }

    [Fact]
    public async Task InlineSecrets_AreInTheChartsSecret_AndNotInValues()
    {
        using var dir = await GenerateAsync(manifest: Fixtures.ManifestWithSecret());

        dir.Read("helm", "pg", "templates", "secret.yaml").ShouldContain(Fixtures.Secret);
        dir.Read("helm", "pg", "values.yaml").ShouldNotContain(Fixtures.Secret);
        dir.Read("helm", "pg", "templates", "configmap.yaml").ShouldNotContain(Fixtures.Secret);
    }

    [Fact]
    public async Task WithAnExternalBackend_NoSecretValueIsWritten()
    {
        var config = Fixtures.Config();
        config.Secrets.Backend = SecretBackendType.KubernetesSecret;
        using var dir = await GenerateAsync(config, Fixtures.ManifestWithSecret());

        File.Exists(dir.File("helm", "pg", "templates", "secret.yaml")).ShouldBeFalse();
        foreach (var file in Directory.GetFiles(dir.File("helm", "pg"), "*", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).ShouldNotContain(Fixtures.Secret);
        }
    }

    [Fact]
    public async Task Regenerating_DropsTemplatesTheServiceNoLongerHas()
    {
        using var dir = await GenerateAsync(Fixtures.Config(domain: "example.com"));
        File.Exists(dir.File("helm", "api", "templates", "ingress.yaml")).ShouldBeTrue();

        await Target().GenerateAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, TestContext.Current.CancellationToken);

        File.Exists(dir.File("helm", "api", "templates", "ingress.yaml")).ShouldBeFalse();
    }

    [Fact]
    public async Task Templates_UseLfLineEndings()
    {
        using var dir = await GenerateAsync();

        Directory.GetFiles(dir.File("helm", "api"), "*.yaml", SearchOption.AllDirectories)
            .ShouldAllBe(f => !File.ReadAllText(f).Contains('\r'));
    }

    [Fact]
    public void TheChartsFolder_IsSeparateFromThePlainManifests()
        => HelmChartWriter.ChartDirectory("out", "api").ShouldBe(Path.Combine("out", "helm", "api"));

    [Fact]
    public async Task ExistingKubernetesTarget_StillWritesItsOwnManifests()
    {
        // The Helm chart and kubectl apply share the renderers; the namespace stays a kubectl file.
        var svc = (await new KubernetesManifestGenerator()
            .GenerateAsync(Fixtures.Manifest(), Fixtures.Config(), new KubernetesTargetConfig { NamespacePrefix = "test" }, TestContext.Current.CancellationToken))
            .Single(s => s.ServiceName == "api");

        KubernetesManifestGenerator.Render(svc).Select(f => f.FileName).ShouldBe(["namespace.yaml", "configmap.yaml", "deployment.yaml", "service.yaml"]);
    }
}
