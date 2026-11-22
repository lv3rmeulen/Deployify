using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Deployify.Target.GitOps.Tests;

/// <summary>Canary and blue-green on the GitOps targets: Argo Rollouts with Argo CD, Flagger with Flux.</summary>
public sealed class GitOpsStrategyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EnvironmentConfig With(EnvironmentConfig config, RolloutConfig rollout)
    {
        config.ServiceOverrides["api"] = new ServiceOverride { Rollout = rollout };
        return config;
    }

    private static readonly RolloutConfig Canary = new()
    {
        Strategy = RolloutStrategyKind.Canary,
        Canary = new CanaryConfig { Steps = [new() { Weight = 10, Pause = "5m" }, new() { Weight = 50, Pause = "5m" }] }
    };

    private static readonly RolloutConfig BlueGreen = new()
    {
        Strategy = RolloutStrategyKind.BlueGreen,
        BlueGreen = new BlueGreenConfig { PreviewPause = "2m", ScaleDownDelay = "30s" }
    };

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    [Fact]
    public async Task ArgoCd_Canary_CommitsARollout_AndTheApplicationIgnoresWhatItChanges()
    {
        using var dir = new TempDir();
        var config = With(Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout")), Canary);

        await Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var rollout = dir.Read("gitops", "tree", "api", ProgressiveDelivery.ArgoRolloutsFile);
        Parse(rollout);
        rollout.ShouldContain("kind: Rollout");
        rollout.ShouldContain("    kind: Deployment\n    name: api\n    scaleDown: progressively");
        rollout.ShouldContain("        - setWeight: 10\n        - pause:\n            duration: 300s\n        - setWeight: 50");
        dir.Read("gitops", "tree", "api", "kustomization.yaml").ShouldContain("  - rollout.yaml");

        var app = dir.Read("gitops", "tree", "_registration", "api.yaml");
        Parse(app);
        app.ShouldContain("      - RespectIgnoreDifferences=true");
        app.ShouldContain("  ignoreDifferences:");
        app.ShouldContain("      - /spec/replicas");
        app.ShouldNotContain("kind: Service");

        // The worker keeps its plain manifests.
        File.Exists(dir.File("gitops", "tree", "worker", ProgressiveDelivery.ArgoRolloutsFile)).ShouldBeFalse();
        dir.Read("gitops", "tree", "_registration", "worker.yaml").ShouldNotContain("ignoreDifferences");
    }

    [Fact]
    public async Task ArgoCd_BlueGreen_AddsAPreviewService()
    {
        using var dir = new TempDir();
        var config = With(Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout")), BlueGreen);

        await Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var rollout = dir.Read("gitops", "tree", "api", ProgressiveDelivery.ArgoRolloutsFile);
        Parse(rollout);
        rollout.ShouldContain("      activeService: api\n      previewService: api-preview");
        rollout.ShouldContain("      scaleDownDelaySeconds: 30");
        rollout.ShouldContain("      autoPromotionSeconds: 120");
        var preview = dir.Read("gitops", "tree", "api", ProgressiveDelivery.PreviewServiceFile);
        preview.ShouldContain("name: api-preview");
        preview.ShouldContain("app: api\n");
        Parse(dir.Read("gitops", "tree", "_registration", "api.yaml")).ShouldNotBeNull();
        dir.Read("gitops", "tree", "_registration", "api.yaml").ShouldContain("    - group: \"\"\n      kind: Service\n      name: api-preview");
    }

    [Fact]
    public async Task ArgoCd_BlueGreen_NeedsAService()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));
        config.ServiceOverrides["worker"] = new ServiceOverride { Rollout = BlueGreen };

        var reason = await ((IRolloutStrategyTarget)Fixtures.Argo(new FakeProcessRunner())).GetStrategyUnsupportedReasonAsync(
            Fixtures.Manifest(), config, "worker", RolloutStrategies.For(config, "worker"), Ct);

        reason!.ShouldContain("no port");
    }

    [Fact]
    public async Task Flux_BlueGreen_HandsTheServiceToFlagger()
    {
        using var dir = new TempDir();
        var config = With(Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout")), BlueGreen);

        await Fixtures.Flux(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        File.Exists(dir.File("gitops", "tree", "api", "service.yaml")).ShouldBeFalse();
        dir.Read("gitops", "tree", "api", "deployment.yaml").ShouldNotContain("replicas:");
        var canary = dir.Read("gitops", "tree", "api", ProgressiveDelivery.FlaggerCanaryFile);
        Parse(canary);
        canary.ShouldContain("kind: Canary");
        canary.ShouldContain("  provider: \"kubernetes\"");
        canary.ShouldContain("    portName: http");
        canary.ShouldContain("    interval: 60s");
        canary.ShouldContain("    iterations: 2");
        var hpa = dir.Read("gitops", "tree", "api", ProgressiveDelivery.FlaggerHpaFile);
        Parse(hpa);
        hpa.ShouldContain("  minReplicas: 1\n  maxReplicas: 1");
        dir.Read("gitops", "tree", "api", "kustomization.yaml").ShouldNotContain("service.yaml");
    }

    [Fact]
    public async Task Flux_Canary_NeedsAProviderThatRoutesTraffic()
    {
        using var dir = new TempDir();
        var config = With(Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout")), Canary);
        var flux = (IRolloutStrategyTarget)Fixtures.Flux(new FakeProcessRunner());

        (await flux.GetStrategyUnsupportedReasonAsync(Fixtures.Manifest(), config, "api", RolloutStrategies.For(config, "api"), Ct))!
            .ShouldContain("provider");

        var withNginx = With(Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"),
            controller: new { flagger = new { provider = "nginx" } }), Canary);
        (await flux.GetStrategyUnsupportedReasonAsync(Fixtures.Manifest(), withNginx, "api", RolloutStrategies.For(withNginx, "api"), Ct))
            .ShouldBeNull();

        await Fixtures.Flux(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), withNginx, dir.Path, Ct);
        var canary = dir.Read("gitops", "tree", "api", ProgressiveDelivery.FlaggerCanaryFile);
        canary.ShouldContain("    stepWeights: [10, 50]");
        canary.ShouldContain("    interval: 300s");
    }

    [Fact]
    public void Flagger_RefusesStepsWithDifferentPauses()
    {
        var config = new EnvironmentConfig
        {
            Rollout = new RolloutConfig
            {
                Strategy = RolloutStrategyKind.Canary,
                Canary = new CanaryConfig { Steps = [new() { Weight = 10, Pause = "1m" }, new() { Weight = 50, Pause = "5m" }] }
            }
        };
        var svc = new Deployify.Target.Kubernetes.KubernetesServiceManifest
        {
            ServiceName = "api", Ports = [new() { Name = "http", ContainerPort = 8080 }]
        };

        ProgressiveDelivery.FlaggerUnsupportedReason(svc, RolloutStrategies.For(config, "api"), new FlaggerConfig { Provider = "istio" })!
            .ShouldContain("same pause");
    }
}
