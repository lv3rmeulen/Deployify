using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.OvhCloud.Tests;

/// <summary>The cluster is found and reached through the OVHcloud API; the rest is the Kubernetes target's work.</summary>
public sealed class OvhCloudDeploymentTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OvhCloudDeploymentTarget Target(FakeProcessRunner kubectl, FakeOvhApi api)
        => new(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, kubectl, api) { ReadyPollInterval = TimeSpan.Zero };

    private static bool PassesKubeconfig(Core.Processes.ProcessSpec call, string path)
    {
        var args = call.Args.ToList();
        var i = args.IndexOf("--kubeconfig");
        return i >= 0 && i + 1 < args.Count && args[i + 1] == path;
    }

    [Fact]
    public void TheTargetId_IsBuiltIn()
    {
        DeploymentTargetType.Parse("ovhcloud").ShouldBe(DeploymentTargetType.OvhCloud);
        DeploymentTargetType.OvhCloud.IsBuiltIn.ShouldBeTrue();
        DeploymentTargetType.OvhCloud.Value.ShouldBe("OvhCloud");
    }

    [Fact]
    public async Task Apply_FetchesTheKubeconfig_AndHandsItToKubectl()
    {
        using var dir = new TempDir();
        var kubeconfig = dir.File("kube", "cluster.kubeconfig");
        var kubectl = new FakeProcessRunner();
        var api = FakeOvhApi.Cluster("READY");
        var target = Target(kubectl, api);
        var config = Fixtures.Config(kubeconfig);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);

        results.ShouldAllBe(r => r.Success);
        File.ReadAllText(kubeconfig).ShouldBe(FakeOvhApi.Kubeconfig);
        var applies = kubectl.Calls.Where(c => c.FileName == "kubectl" && c.Args.Contains("apply")).ToList();
        applies.ShouldNotBeEmpty();
        applies.ShouldAllBe(c => PassesKubeconfig(c, kubeconfig));
    }

    [Fact]
    public async Task TheKubeconfig_IsFetchedOncePerRun()
    {
        using var dir = new TempDir();
        var api = FakeOvhApi.Cluster("READY");
        var target = Target(new FakeProcessRunner(), api);
        var config = Fixtures.Config(dir.File("k.kubeconfig"));

        await target.PlanAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);
        await target.ApplyAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);
        await target.ListManagedAsync(config, Ct);

        api.To("/1.0/cloud/project/proj1/kube/kube1/kubeconfig").Count().ShouldBe(1);
    }

    [Fact]
    public async Task TheStateConfig_IsLeftAlone_ButSecretReferencesAreShared()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(dir.File("k.kubeconfig"));
        var before = JsonSerializer.Serialize(config.TargetConfig["kubernetes"]);
        var target = Target(new FakeProcessRunner(), FakeOvhApi.Cluster("READY"));

        var effective = await target.ConnectAsync(config, Ct);

        JsonSerializer.Serialize(config.TargetConfig["kubernetes"]).ShouldBe(before);
        Kubernetes.KubernetesDeploymentTarget.GetConfig(effective).KubeConfigPath.ShouldBe(dir.File("k.kubeconfig"));
        Kubernetes.KubernetesDeploymentTarget.GetConfig(effective).NamespacePrefix.ShouldBe("test");
        effective.Secrets.ShouldBeSameAs(config.Secrets);
    }

    [Fact]
    public async Task AClusterBeingInstalled_IsWaitedFor()
    {
        using var dir = new TempDir();
        var api = FakeOvhApi.Cluster("INSTALLING", "INSTALLING", "READY");
        var target = Target(new FakeProcessRunner(), api);

        await target.ConnectAsync(Fixtures.Config(dir.File("k.kubeconfig")), Ct);

        api.To("/1.0/cloud/project/proj1/kube/kube1").Count().ShouldBe(3);
        File.Exists(dir.File("k.kubeconfig")).ShouldBeTrue();
    }

    [Fact]
    public async Task AClusterInError_FailsEveryService_BeforeKubectlRuns()
    {
        using var dir = new TempDir();
        var kubectl = new FakeProcessRunner();
        var target = Target(kubectl, FakeOvhApi.Cluster("ERROR"));
        var config = Fixtures.Config(dir.File("k.kubeconfig"));
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);

        results.Select(r => r.ServiceName).ShouldBe(["api", "redis"], ignoreOrder: true);
        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("ERROR"));
        kubectl.Calls.ShouldNotContain(c => c.Args.Contains("apply"));
        File.Exists(dir.File("k.kubeconfig")).ShouldBeFalse();
    }

    [Fact]
    public async Task Plan_WithoutCredentials_WritesTheArtifacts_AndMarksServicesUnchecked()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), new FakeOvhApi());
        var config = Fixtures.Config(dir.File("k"), ovhcloud: new { projectId = "proj1", clusterId = "kube1" });

        var plan = await target.PlanAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);

        plan.Unknown.Select(u => u.ServiceName).ShouldBe(["api", "redis"], ignoreOrder: true);
        plan.Unknown.ShouldAllBe(u => u.Reason!.Contains("OVH_APPLICATION_KEY"));
        File.Exists(dir.File("out", "api", "deployment.yaml")).ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnreachableCluster_MakesTheRolloutUnavailable_WithTheReason()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), FakeOvhApi.Cluster("SUSPENDED"));

        var reason = await target.GetUnavailableReasonAsync(Fixtures.Config(dir.File("k")), Ct);

        reason!.ShouldContain("SUSPENDED");
    }

    [Fact]
    public async Task Ping_ReportsAClusterThatIsNotReady()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), FakeOvhApi.Cluster("UPDATING"));

        var (ok, error) = await target.PingAsync(Fixtures.Config(dir.File("k.kubeconfig")), Ct);

        ok.ShouldBeFalse();
        error!.ShouldContain("UPDATING");
    }

    [Fact]
    public async Task Ping_ChecksTheClusterThroughKubectl_OnceItIsReady()
    {
        using var dir = new TempDir();
        var kubectl = new FakeProcessRunner();
        var target = Target(kubectl, FakeOvhApi.Cluster("READY"));

        var (ok, _) = await target.PingAsync(Fixtures.Config(dir.File("k.kubeconfig")), Ct);

        ok.ShouldBeTrue();
        kubectl.Calls.ShouldContain(c => PassesKubeconfig(c, dir.File("k.kubeconfig")));
    }

    [Fact]
    public async Task Generate_NeedsNoCredentials()
    {
        using var dir = new TempDir();
        var api = new FakeOvhApi();
        var target = Target(new FakeProcessRunner(), api);
        var config = Fixtures.Config(dir.File("k.kubeconfig"), ovhcloud: new { clusterId = "kube1" });

        var files = await target.GenerateAsync(Fixtures.Manifest(), config, dir.File("out"), Ct);
        var explained = await target.ExplainAsync(Fixtures.Manifest(), config, Ct);

        files.ShouldContain(f => f.EndsWith("deployment.yaml", StringComparison.Ordinal));
        explained.ShouldAllBe(e => e.AdditionalInfo["ovhcloud cluster"] == "kube1");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutAClusterId_ADownloadedKubeconfigIsUsedAsItIs()
    {
        using var dir = new TempDir();
        var api = new FakeOvhApi();
        var kubectl = new FakeProcessRunner();
        var target = Target(kubectl, api);
        var config = Fixtures.Config(dir.File("unused"), ovhcloud: new { endpoint = "ovh-eu" },
            kubernetes: new { namespacePrefix = "test", kubeConfigPath = "/home/me/.kube/ovh.yml" });

        await target.ListManagedAsync(config, Ct);

        api.Requests.ShouldBeEmpty();
        kubectl.Calls.ShouldContain(c => PassesKubeconfig(c, "/home/me/.kube/ovh.yml"));
    }

    [Fact]
    public async Task WithNeitherAClusterNorAKubeconfig_TheErrorSaysWhatToSet()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), new FakeOvhApi());
        var config = Fixtures.Config(dir.File("unused"), ovhcloud: new { endpoint = "ovh-eu" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => target.ListManagedAsync(config, Ct));

        ex.Message.ShouldContain("clusterId");
        ex.Message.ShouldContain("kubeConfigPath");
    }

    [Fact]
    public async Task Destroy_KeepsTheCluster()
    {
        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), new FakeOvhApi());

        var kept = await target.DescribeKeptAsync(Fixtures.Config(dir.File("k")), new DeleteOptions(), Ct);

        kept.ShouldContain(k => k.Contains("OVHcloud cluster kube1"));
    }

    [Fact]
    public void TheDefaultKubeconfigPath_IsPerCluster_AndSafeAsAFileName()
    {
        var path = OvhCloudDeploymentTarget.DefaultKubeconfigPath("proj/1", "kube 1");

        Path.GetFileName(path).ShouldBe("proj_1-kube_1.kubeconfig");
        path.ShouldContain(Path.Combine("deployify", "ovhcloud"));
    }

    [Fact]
    public async Task TheKubeconfig_IsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var target = Target(new FakeProcessRunner(), FakeOvhApi.Cluster("READY"));

        await target.ConnectAsync(Fixtures.Config(dir.File("k.kubeconfig")), Ct);

        File.GetUnixFileMode(dir.File("k.kubeconfig")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void LiteralCredentials_AreFlaggedInTheStateFile()
    {
        var state = new DeployifyStateFile { Environments = { ["prod"] = Fixtures.Config("k") } };

        var found = StateFileSecrets.FindLiteral(state);

        found.ShouldContain("prod.ovhcloud.applicationSecret");
        found.ShouldContain("prod.ovhcloud.consumerKey");
    }
}
