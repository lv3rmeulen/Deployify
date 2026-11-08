using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

public sealed class KubectlRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KubectlRunner Kubectl(FakeProcessRunner fake, string? context = null, string? kubeconfig = null)
        => new(new KubernetesTargetConfig { Context = context, KubeConfigPath = kubeconfig },
            NullLogger.Instance, fake);

    [Fact]
    public async Task ContextAndKubeconfig_AreSeparateArguments_EvenWithSpaces()
    {
        var fake = new FakeProcessRunner();

        await Kubectl(fake, context: "my ctx", kubeconfig: @"C:\Users\me\kube config").RolloutRestartAsync("api", "shop-api", Ct);

        fake.Calls.Single().FileName.ShouldBe("kubectl");
        fake.Calls.Single().Args.ShouldBe(
            ["--kubeconfig", @"C:\Users\me\kube config", "--context", "my ctx",
             "rollout", "restart", "deployment/api", "-n", "shop-api"]);
    }

    [Fact]
    public async Task Jsonpath_IsPassedWithoutQuotes()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "shop-api shop-web", "") };

        var names = await Kubectl(fake).GetNamespacesWithPrefixAsync("shop", Ct);

        names.ShouldBe(["shop-api", "shop-web"]);
        fake.Calls.Single().Args.ShouldContain("jsonpath={.items[*].metadata.name}");
    }

    [Fact]
    public async Task NamespaceLookup_Throws_WhenKubectlFails()
    {
        // An empty list would make every service look new.
        var fake = new FakeProcessRunner { Default = new ProcessResult(1, "", "Unable to connect to the server") };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Kubectl(fake).GetNamespacesWithPrefixAsync("shop", Ct));

        ex.Message.ShouldContain("Unable to connect to the server");
    }

    [Fact]
    public async Task SecretRead_IsNotLogged()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "", "") };

        await Kubectl(fake).GetSecretDataAsync("shop-system", "params", Ct);

        fake.Calls.Single().Sensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task ServerSideApply_SendsObjectOnStdin_NotOnDisk()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "42", "") };

        var version = await Kubectl(fake).ServerSideApplyAsync("{\"kind\":\"Secret\"}", Ct);

        version.ShouldBe("42");
        var call = fake.Calls.Single();
        call.Stdin.ShouldBe("{\"kind\":\"Secret\"}");
        call.Sensitive.ShouldBeTrue();
        call.Args.ShouldContain("-");
    }

    [Theory]
    [InlineData(0, KubectlDiffKind.Same)]
    [InlineData(1, KubectlDiffKind.Different)]
    [InlineData(2, KubectlDiffKind.Failed)]
    public async Task Diff_MapsKubectlExitCodes(int exitCode, KubectlDiffKind expected)
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(exitCode, "-  replicas: 1\n+  replicas: 2", "error: boom") };

        var diff = await Kubectl(fake).DiffFileAsync("deployify-output/api.yaml", Ct);

        diff.Kind.ShouldBe(expected);
        diff.Text.ShouldBe(expected switch
        {
            KubectlDiffKind.Same => "",
            KubectlDiffKind.Different => "-  replicas: 1\n+  replicas: 2",
            _ => "error: boom"
        });
    }

    [Fact]
    public void DiffFailure_WithoutDiffProgram_ExplainsTheFix()
        => KubectlRunner.DescribeDiffFailure("error: failed to run \"diff\": executable file not found in %PATH%")
            .ShouldContain("KUBECTL_EXTERNAL_DIFF");

    [Fact]
    public async Task IsAvailable_FalseWhenKubectlMissing()
    {
        var fake = new FakeProcessRunner { Missing = { "kubectl" } };

        (await KubectlRunner.IsAvailableAsync(fake, Ct)).ShouldBeFalse();
    }
}

/// <summary>Plan and diff share one comparison; these pin how each kubectl outcome is reported.</summary>
public sealed class KubernetesPlanTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private static KubernetesDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance) { Processes = fake };

    /// <summary>kubectl that knows the given namespaces and answers every diff with <paramref name="diffExit"/>.</summary>
    private static FakeProcessRunner Cluster(int diffExit, string diffStderr = "", params string[] namespaces)
        => new FakeProcessRunner()
            .When(["get", "namespaces"], new ProcessResult(0, string.Join(' ', namespaces), ""))
            .When(["diff"], new ProcessResult(diffExit, diffExit == 1 ? "+ replicas: 2" : "", diffStderr));

    [Fact]
    public async Task DiffFailure_IsUnknown_NotUpdate()
    {
        var fake = Cluster(2, "error: failed to run \"diff\": executable file not found in %PATH%", "test-api");

        var plan = await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), _dir.Path, Ct);

        plan.ToUpdate.ShouldBeEmpty();
        plan.Unknown.ShouldHaveSingleItem().ServiceName.ShouldBe("api");
        plan.Unknown[0].Reason.ShouldContain("diff");
        plan.IsUpToDate.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, DiffKind.Unchanged)]
    [InlineData(1, DiffKind.Changed)]
    [InlineData(2, DiffKind.Unknown)]
    public async Task PlanAndDiff_Agree(int diffExit, DiffKind expected)
    {
        var target = Target(Cluster(diffExit, "boom", "test-api"));
        var manifest = Fixtures.SingleService("api");
        var config = Fixtures.KubernetesConfig();

        var diff = await target.DiffAsync(manifest, config, _dir.Path, Ct);
        var plan = await target.PlanAsync(manifest, config, _dir.Path, Ct);

        diff.ShouldHaveSingleItem().Kind.ShouldBe(expected);
        var bucket = expected switch
        {
            DiffKind.Unchanged => plan.Unchanged,
            DiffKind.Changed => plan.ToUpdate,
            _ => [.. plan.UnknownNames]
        };
        bucket.ShouldBe(["api"]);
    }

    [Fact]
    public async Task MissingNamespace_IsCreate_ExtraNamespace_IsDelete_CaseInsensitive()
    {
        var plan = await Target(Cluster(0, "", "TEST-API", "test-old"))
            .PlanAsync(Fixtures.SimpleManifest(), Fixtures.KubernetesConfig(), _dir.Path, Ct);

        plan.Unchanged.ShouldContain("api");
        plan.ToCreate.ShouldNotContain("api");
        plan.ToDelete.ShouldBe(["old"]);
    }

    [Fact]
    public async Task UnreachableCluster_AssumesAllNew()
    {
        var fake = new FakeProcessRunner().When(["get", "namespaces"], new ProcessResult(1, "", "connection refused"));

        var plan = await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(), _dir.Path, Ct);

        plan.ToCreate.ShouldBe(["api"]);
        fake.Calls.ShouldNotContain(c => c.Args.Contains("diff"));
    }

    [Fact]
    public async Task Diff_RunsOnTheServiceDirectory_WithKubeContext()
    {
        var fake = Cluster(0, "", "test-api");

        await Target(fake).PlanAsync(Fixtures.SingleService("api"), Fixtures.KubernetesConfig(context: "prod cluster"), _dir.Path, Ct);

        fake.Single("diff").Args.ShouldBe(["--context", "prod cluster", "diff", "-R", "-f", Path.Combine(_dir.Path, "api")]);
    }
}
