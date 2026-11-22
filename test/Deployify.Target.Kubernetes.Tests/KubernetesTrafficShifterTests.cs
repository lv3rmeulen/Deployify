using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Target.Kubernetes.Manifests;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubernetesTrafficShifterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    private static KubernetesDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance) { Processes = fake };

    private static RolloutStrategy Strategy(RolloutStrategyKind kind)
        => RolloutStrategies.For(new EnvironmentConfig { Rollout = new RolloutConfig { Strategy = kind } }, "api");

    private static string Deployment(string name, long generation, int replicas, int available)
        => new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = name, ["generation"] = generation },
            ["spec"] = new JsonObject { ["replicas"] = replicas },
            ["status"] = new JsonObject
            {
                ["observedGeneration"] = generation, ["replicas"] = replicas,
                ["updatedReplicas"] = replicas, ["availableReplicas"] = available
            }
        }.ToJsonString();

    private static string Service(string app)
        => new JsonObject { ["spec"] = new JsonObject { ["selector"] = new JsonObject { ["app"] = app } } }.ToJsonString();

    private static async Task<KubernetesServiceManifest> ApiAsync()
    {
        var config = Fixtures.KubernetesConfig();
        return (await new KubernetesManifestGenerator().GenerateAsync(
            Fixtures.SimpleManifest(), config, KubernetesDeploymentTarget.GetConfig(config), Ct)).Single(s => s.ServiceName == "api");
    }

    [Theory]
    [InlineData(3, 10, 1, 25)]
    [InlineData(3, 50, 3, 50)]
    [InlineData(1, 10, 1, 50)]
    [InlineData(9, 10, 1, 10)]
    [InlineData(4, 20, 1, 20)]
    [InlineData(2, 75, 6, 75)]
    public void CanaryReplicas_FollowTheWeight(int stable, int percent, int replicas, int share)
        => KubernetesTrafficShifter.CanaryReplicas(stable, percent).ShouldBe((replicas, share));

    [Fact]
    public async Task CanaryDeployment_SharesTheServiceSelector_AndReadsItsOwnConfigMap()
    {
        var svc = await ApiAsync();
        var deployment = KubernetesManifestBuilder.ToText(KubernetesManifestBuilder.Build(svc).Single(f => f.FileName == "deployment.yaml").Model);

        var yaml = CandidateManifests.Deployment(deployment, "api", "api-canary", preview: false, replicas: 1,
            "api-config-candidate", "api-config");

        yaml.ShouldContain("name: api-canary");
        yaml.ShouldContain("replicas: 1");
        yaml.ShouldContain("deployify/track: canary");
        yaml.ShouldContain("deployify/candidate-of: api");
        yaml.ShouldContain("name: api-config-candidate");
        yaml.ShouldNotContain("name: api-config\n");
        yaml.ShouldContain("app: api\n");
        yaml.ShouldNotEndWith("...\n");
        KubernetesYaml.ReadMetadata(yaml).Kind.ShouldBe("Deployment");
    }

    [Fact]
    public async Task PreviewDeployment_GetsItsOwnAppLabel()
    {
        var svc = await ApiAsync();
        var deployment = KubernetesManifestBuilder.ToText(KubernetesManifestBuilder.Build(svc).Single(f => f.FileName == "deployment.yaml").Model);

        var yaml = CandidateManifests.Deployment(deployment, "api", "api-preview", preview: true, replicas: 3, null, "api-config");

        // The selector and the pod labels; the object's own labels keep app: api.
        System.Text.RegularExpressions.Regex.Count(yaml, "app: api-preview\n").ShouldBe(2);
        System.Text.RegularExpressions.Regex.Count(yaml, "app: api\n").ShouldBe(1);
        yaml.ShouldNotContain("deployify/track");
        yaml.ShouldContain("replicas: 3");
    }

    [Fact]
    public async Task StartCanary_AppliesConfigCopyAndOnePodDeployment()
    {
        var fake = new FakeProcessRunner()
            .When(["get", "deployment", "api"], Ok(Deployment("api", 4, 3, 3)))
            .When(["get", "service", "api"], Ok(Service("api")));
        var svc = await ApiAsync();

        var started = await Target(fake).StartCandidateAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), "out", "api",
            Strategy(RolloutStrategyKind.Canary), new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "4" }, Ct);

        started.Success.ShouldBeTrue(started.Error);
        started.Revision.ShouldBe("api-canary");
        var applied = fake.Calls.Where(c => c.Args.Contains("apply") && c.Stdin is not null).Select(c => c.Stdin!).ToList();
        applied.ShouldContain(y => y.Contains("kind: ConfigMap") && y.Contains("name: api-config-candidate"));
        applied.ShouldContain(y => y.Contains("kind: Deployment") && y.Contains("name: api-canary") && y.Contains("replicas: 1"));
        // Leftovers of an interrupted rollout are removed first.
        fake.Calls.ShouldContain(c => c.Args.SequenceEqual(new[] { "delete", "deployment", "api-canary", "-n", svc.Namespace, "--ignore-not-found" }));
    }

    [Fact]
    public async Task ShiftCanary_ScalesByWeight_AndReportsTheRealShare()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api"], Ok(Deployment("api", 4, 3, 3)));

        var share = await Target(fake).ShiftTrafficAsync(Fixtures.KubernetesConfig(), "api", "api-canary", 10, Ct);

        share.ShouldBe(25);
        fake.Calls.ShouldContain(c => c.Args.Contains("scale") && c.Args.Contains("deployment/api-canary") && c.Args.Contains("--replicas=1"));
    }

    [Fact]
    public async Task ShiftPreview_SwitchesTheServiceSelector()
    {
        var fake = new FakeProcessRunner().When(["get", "service", "api"], Ok(Service("api")));

        var share = await Target(fake).ShiftTrafficAsync(Fixtures.KubernetesConfig(), "api", "api-preview", 100, Ct);

        share.ShouldBe(100);
        var patch = fake.Calls.Single(c => c.Args.Contains("patch"));
        patch.Args.ShouldContain("""{"spec":{"selector":{"app":"api-preview"}}}""");
    }

    [Fact]
    public async Task ObserveCandidate_JudgesTheLatestGeneration()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api-canary"], Ok(Deployment("api-canary", 9, 2, 2)));

        var obs = await Target(fake).ObserveCandidateAsync(Fixtures.KubernetesConfig(), "api", "api-canary", Ct);

        obs.State.ShouldBe(HealthState.Healthy);
    }

    [Fact]
    public async Task Restore_SwitchesTrafficBackAndDeletesTheCandidate()
    {
        var fake = new FakeProcessRunner()
            .When(["get", "service", "api"], Ok(Service("api-preview")))
            .When(["get", "deployment", "api"], Ok(Deployment("api", 4, 3, 3)));

        await Target(fake).RestoreAsync(Fixtures.KubernetesConfig(),
            new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "4" }, Ct);

        var patch = fake.Calls.FindIndex(c => c.Args.Contains("patch") && c.Args.Contains("""{"spec":{"selector":{"app":"api"}}}"""));
        var delete = fake.Calls.FindIndex(c => c.Args.Contains("delete") && c.Args.Contains("api-preview"));
        patch.ShouldBeGreaterThanOrEqualTo(0);
        delete.ShouldBeGreaterThan(patch);
    }
}
