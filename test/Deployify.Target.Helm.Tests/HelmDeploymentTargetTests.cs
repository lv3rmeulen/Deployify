using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Helm.Tests;

/// <summary>Plan, apply, rollout and destroy as helm and kubectl calls.</summary>
public sealed class HelmDeploymentTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HelmDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, fake);

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    private static string Deployment(long generation)
        => "{\"metadata\":{\"name\":\"api\",\"generation\":" + generation + "},\"spec\":{\"replicas\":1},\"status\":{}}";

    private static string Status(int revision, string status = "deployed")
        => "{\"name\":\"api\",\"namespace\":\"test-api\",\"version\":" + revision + ",\"info\":{\"status\":\"" + status + "\"}}";

    /// <summary>A cluster with the given releases (name, namespace) and Deployify's namespaces.</summary>
    private static FakeProcessRunner Cluster(params (string Name, string Namespace)[] releases)
        => new FakeProcessRunner()
            .When(["list", "--all-namespaces"], Ok("[" + string.Join(",", releases.Select(r =>
                $$"""{"name":"{{r.Name}}","namespace":"{{r.Namespace}}","revision":"3","status":"deployed"}""")) + "]"))
            .When(["get", "namespaces", "-l", "app.kubernetes.io/managed-by=deployify"],
                Ok(string.Join(' ', releases.Select(r => r.Namespace))));

    private static ProcessSpec HelmCall(FakeProcessRunner fake, string command)
        => fake.Calls.Single(c => c.FileName == "helm" && c.Args.Contains(command));

    // ── apply ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_CreatesTheNamespace_ThenUpgradesOrInstallsTheRelease()
    {
        var fake = new FakeProcessRunner();
        using var dir = new TempDir();
        var target = Target(fake);
        var config = Fixtures.Config();
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => r.Success);
        var upgrade = fake.Calls.First(c => c.FileName == "helm" && c.Args.Contains("upgrade") && c.Args.Contains("api")).Args.ToList();
        upgrade.ShouldBe(["upgrade", "--install", "api", dir.File("helm", "api"), "--namespace", "test-api", "--history-max", "10"]);

        // The namespace is applied (with Deployify's label) before the release that lives in it.
        var nsApply = fake.Calls.FindIndex(c => c.FileName == "kubectl" && c.Stdin is { } s && s.Contains("\"test-api\"") && s.Contains("Namespace"));
        nsApply.ShouldBeGreaterThanOrEqualTo(0);
        fake.Calls[nsApply].Stdin!.ShouldContain("app.kubernetes.io/managed-by");
        nsApply.ShouldBeLessThan(fake.Calls.FindIndex(c => c.FileName == "helm" && c.Args.Contains("upgrade") && c.Args.Contains("api")));
    }

    [Fact]
    public async Task Apply_PassesTheContext_AndWaitWhenConfigured()
    {
        var fake = new FakeProcessRunner();
        using var dir = new TempDir();
        var target = Target(fake);
        var config = Fixtures.Config(context: "prod", helm: new { wait = true, timeout = "2m", historyMax = 3 });
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var args = fake.Calls.First(c => c.FileName == "helm" && c.Args.Contains("upgrade")).Args.ToList();
        args.Take(2).ShouldBe(["--kube-context", "prod"]);
        args.ShouldContain("--wait");
        args[args.IndexOf("--timeout") + 1].ShouldBe("2m");
        args[args.IndexOf("--history-max") + 1].ShouldBe("3");
    }

    [Fact]
    public async Task Apply_WithoutAChart_AsksForPlanFirst()
    {
        using var dir = new TempDir();

        var results = await Target(new FakeProcessRunner()).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("deployify plan"));
    }

    [Fact]
    public async Task Apply_WithoutHelm_FailsEveryService_WithTheReason()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("helm");
        using var dir = new TempDir();

        var results = await Target(fake).ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("needs helm"));
        fake.Calls.ShouldNotContain(c => c.Args.Contains("upgrade"));
    }

    [Fact]
    public async Task AFailedUpgrade_IsReported_WithHelmsError()
    {
        var fake = new FakeProcessRunner().When(["upgrade", "--install"], new ProcessResult(1, "", "Error: UPGRADE FAILED: boom"));
        using var dir = new TempDir();
        var target = Target(fake);
        await target.GenerateAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("UPGRADE FAILED"));
    }

    [Fact]
    public async Task HelmOutput_IsNeverLogged_ForCallsThatCanCarrySecrets()
    {
        var fake = new FakeProcessRunner();
        using var dir = new TempDir();
        var target = Target(fake);
        await target.GenerateAsync(Fixtures.ManifestWithSecret(), Fixtures.Config(), dir.Path, Ct);

        await target.ApplyAsync(Fixtures.ManifestWithSecret(), Fixtures.Config(), dir.Path, Ct);

        HelmCall(fake, "upgrade").Sensitive.ShouldBeTrue();
    }

    // ── plan / diff ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_AddsChangesAndRemoves_ByRelease()
    {
        var fake = Cluster(("api", "test-api"), ("old", "test-old"), ("someone-else", "other"))
            .When(["diff", "upgrade", "api"], new ProcessResult(2, "-  replicas: 1\n+  replicas: 2", ""));
        using var dir = new TempDir();

        var entries = await Target(fake).DiffAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        entries.Single(e => e.ServiceName == "api").Kind.ShouldBe(DiffKind.Changed);
        entries.Single(e => e.ServiceName == "worker").Kind.ShouldBe(DiffKind.Added);
        entries.Single(e => e.ServiceName == "old").Kind.ShouldBe(DiffKind.Removed);
        // A release outside Deployify's layout is never ours to remove.
        entries.ShouldNotContain(e => e.ServiceName == "someone-else");
        HelmCall(fake, "diff").Args.ShouldContain("--detailed-exitcode");
    }

    [Fact]
    public async Task Diff_ReportsUnchanged_WhenHelmDiffFindsNothing()
    {
        var fake = Cluster(("api", "test-api")).When(["diff", "upgrade"], Ok());
        using var dir = new TempDir();

        var entries = await Target(fake).DiffAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        entries.Single(e => e.ServiceName == "api").Kind.ShouldBe(DiffKind.Unchanged);
    }

    [Fact]
    public async Task Diff_WithoutTheHelmDiffPlugin_IsUnknown_AndSaysHowToInstallIt()
    {
        var fake = Cluster(("api", "test-api"))
            .When(["diff", "upgrade"], new ProcessResult(1, "", "Error: unknown command \"diff\" for \"helm\""));
        using var dir = new TempDir();

        var api = (await Target(fake).DiffAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct)).Single(e => e.ServiceName == "api");

        api.Kind.ShouldBe(DiffKind.Unknown);
        api.Reason!.ShouldContain("helm plugin install");
    }

    [Fact]
    public async Task Plan_WithoutHelm_IsUnknown_RatherThanGuessed()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("helm");
        using var dir = new TempDir();

        var plan = await Target(fake).PlanAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        plan.UnknownCount.ShouldBe(3);
        plan.IsUpToDate.ShouldBeFalse();
    }

    // ── rollout ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_RecordsTheReleaseRevision()
    {
        var fake = new FakeProcessRunner().When(["status", "api"], Ok(Status(3)));

        var snapshot = await Target(fake).CaptureAsync(Fixtures.Manifest(), Fixtures.Config(), "api", Ct);

        snapshot.Existed.ShouldBeTrue();
        snapshot.Revision.ShouldBe("3");
        snapshot.Data["namespace"].ShouldBe("test-api");
    }

    [Fact]
    public async Task Capture_OfANewService_HasNothingToGoBackTo()
    {
        var fake = new FakeProcessRunner().When(["status", "api"], new ProcessResult(1, "", "Error: release: not found"));

        var snapshot = await Target(fake).CaptureAsync(Fixtures.Manifest(), Fixtures.Config(), "api", Ct);

        snapshot.Existed.ShouldBeFalse();
    }

    [Fact]
    public async Task Capture_Throws_WhenHelmCantAskTheCluster()
    {
        var fake = new FakeProcessRunner().When(["status", "api"], new ProcessResult(1, "", "Error: Kubernetes cluster unreachable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => Target(fake).CaptureAsync(Fixtures.Manifest(), Fixtures.Config(), "api", Ct));
    }

    [Fact]
    public async Task ApplyService_ReturnsTheDeploymentGeneration_ForTheHealthGate()
    {
        var fake = new FakeProcessRunner().When(["get", "deployment", "api"], Ok(Deployment(7)));
        using var dir = new TempDir();
        var target = Target(fake);
        await target.GenerateAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, Ct);

        var applied = await target.ApplyServiceAsync(Fixtures.Manifest(), Fixtures.Config(), dir.Path, "api", Ct);

        applied.Success.ShouldBeTrue();
        applied.Revision.ShouldBe("7");
    }

    [Fact]
    public async Task Restore_RollsTheReleaseBack_AndWaitsOnTheNewGeneration()
    {
        var fake = new FakeProcessRunner()
            .When(["status", "api"], Ok(Status(4)))
            .When(["get", "deployment", "api"], Ok(Deployment(9)));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "3", Data = { ["namespace"] = "test-api" } };

        var revision = await Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct);

        HelmCall(fake, "rollback").Args.ToList().ShouldBe(["rollback", "api", "3", "--namespace", "test-api", "--history-max", "10"]);
        revision.ShouldBe("9");
    }

    [Fact]
    public async Task Restore_IsANoOp_WhenTheReleaseIsAlreadyAtTheRevision()
    {
        var fake = new FakeProcessRunner()
            .When(["status", "api"], Ok(Status(3)))
            .When(["get", "deployment", "api"], Ok(Deployment(5)));
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = true, Revision = "3", Data = { ["namespace"] = "test-api" } };

        (await Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct)).ShouldBe("5");

        fake.Calls.ShouldNotContain(c => c.Args.Contains("rollback"));
    }

    [Fact]
    public async Task Restore_OfAServiceThisRolloutCreated_UninstallsIt()
    {
        var fake = new FakeProcessRunner();
        var snapshot = new ServiceSnapshot { ServiceName = "api", Existed = false, Data = { ["namespace"] = "test-api" } };

        var revision = await Target(fake).RestoreAsync(Fixtures.Config(), snapshot, Ct);

        revision.ShouldBeNull();
        HelmCall(fake, "uninstall").Args.ToList().ShouldBe(["uninstall", "api", "--namespace", "test-api"]);
    }

    [Fact]
    public void TheHealthGate_RollsBackItself_RatherThanWaitingForHelm()
        => Target(new FakeProcessRunner()).HasNativeRollback(Fixtures.Config()).ShouldBeFalse();

    [Fact]
    public async Task Rollouts_AreUnavailable_WithoutKubectl()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("kubectl");

        (await Target(fake).GetUnavailableReasonAsync(Fixtures.Config(), Ct))!.ShouldContain("kubectl");
    }

    // ── destroy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_UninstallsTheRelease_ThenDeletesItsNamespace()
    {
        var fake = Cluster(("api", "test-api"));

        await Target(fake).DeleteServiceAsync(Fixtures.Config(), "api", new DeleteOptions(), Ct);

        var uninstall = fake.Calls.FindIndex(c => c.FileName == "helm" && c.Args.Contains("uninstall"));
        var deleteNs = fake.Calls.FindIndex(c => c.FileName == "kubectl" && c.Args.Contains("delete") && c.Args.Contains("test-api"));
        uninstall.ShouldBeGreaterThanOrEqualTo(0);
        deleteNs.ShouldBeGreaterThanOrEqualTo(0);
        uninstall.ShouldBeLessThan(deleteNs);
    }

    [Fact]
    public async Task Delete_LeavesANamespaceWithoutDeployifysLabelAlone()
    {
        var fake = Cluster(("worker", "test-worker"));

        await Target(fake).DeleteServiceAsync(Fixtures.Config(), "api", new DeleteOptions(), Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("uninstall") || c.Args.Contains("delete"));
    }

    [Fact]
    public async Task Delete_OfAReleaseThatIsAlreadyGone_StillDeletesTheNamespace()
    {
        var fake = Cluster(("api", "test-api"))
            .When(["uninstall", "api"], new ProcessResult(1, "", "Error: uninstall: Release not loaded: api: release: not found"));

        await Target(fake).DeleteServiceAsync(Fixtures.Config(), "api", new DeleteOptions(), Ct);

        fake.Calls.ShouldContain(c => c.FileName == "kubectl" && c.Args.Contains("delete") && c.Args.Contains("test-api"));
    }

    [Fact]
    public async Task ListManaged_IsTheLabelledNamespaces_AsOnKubernetes()
    {
        var managed = await Target(Cluster(("api", "test-api"), ("x", "other"))).ListManagedAsync(Fixtures.Config(), Ct);

        managed.Select(m => m.Name).ShouldBe(["api"]);
    }

    // ── the rest ─────────────────────────────────────────────────────────────

    [Fact]
    public void Explain_NamesTheReleaseAndChart()
    {
        var api = Target(new FakeProcessRunner()).Explain(Fixtures.Manifest(), Fixtures.Config()).Single(e => e.Name == "api");

        api.AdditionalInfo["release"].ShouldBe("test-api/api");
        api.AdditionalInfo["chart"].ShouldBe("helm/api");
    }

    [Fact]
    public void SecretBackends_AreTheKubernetesTargets()
        => Target(new FakeProcessRunner()).SupportedSecretBackends
            .ShouldBe([SecretBackendType.KubernetesSecret, SecretBackendType.Vault, SecretBackendType.KeyVault]);

    [Fact]
    public void ReleaseList_IsParsed_WithItsStringRevisions()
    {
        var releases = HelmRunner.ParseList("""[{"name":"api","namespace":"test-api","revision":"12","status":"failed"}]""");

        releases.ShouldBe([new HelmRelease("api", "test-api", 12, "failed")]);
    }

    [Fact]
    public void TheTarget_IsHelm()
        => Target(new FakeProcessRunner()).TargetType.ShouldBe(DeploymentTargetType.Helm);
}
