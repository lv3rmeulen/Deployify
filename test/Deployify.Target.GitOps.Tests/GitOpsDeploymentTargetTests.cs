using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.GitOps.Tests;

/// <summary>Plan, apply, wait and destroy as git and kubectl calls.</summary>
public sealed class GitOpsDeploymentTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProcessSpec Git(FakeProcessRunner fake, string command)
        => fake.Calls.Single(c => c.FileName == "git" && c.Args.Contains(command));

    // ── apply ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_CommitsTheFolders_Pushes_AndRegistersEachService()
    {
        var fake = Fixtures.Tools();
        using var dir = new TempDir();
        var checkout = dir.File("checkout");
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, checkout);
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => r.Success && r.DeploymentId == "commit/0123456");
        File.Exists(Path.Combine(checkout, "deployify", "test", "api", "deployment.yaml")).ShouldBeTrue();
        File.Exists(Path.Combine(checkout, "deployify", "test", "_registration", "worker.yaml")).ShouldBeTrue();

        Git(fake, "clone").Args.ShouldBe(["clone", "--no-checkout", Fixtures.RepoUrl, checkout]);
        Git(fake, "commit").Args.ShouldContain("deployify/test");
        Git(fake, "push").Args.ShouldContain("HEAD:refs/heads/main");
        Git(fake, "push").Env!["GIT_TERMINAL_PROMPT"].ShouldBe("0");

        // Registered from the committed folder, after the push, with the environment's context.
        var applies = fake.Calls.Where(c => c.FileName == "kubectl" && c.Args.Contains("apply")).ToList();
        applies.Select(c => Path.GetFileName(c.Args[^1])).ShouldBe(["api.yaml", "worker.yaml"]);
        applies.ShouldAllBe(c => c.Args[0] == "--context" && c.Args[1] == "prod");
        fake.Calls.IndexOf(applies[0]).ShouldBeGreaterThan(fake.Calls.IndexOf(Git(fake, "push")));

        // Argo CD is asked to look now.
        fake.Calls.ShouldContain(c => c.Args.Contains("annotate") && c.Args.Contains("argocd.argoproj.io/refresh=normal"));
    }

    [Fact]
    public async Task Apply_OnANewBranch_StartsIt_WithAnOrphanCommit()
    {
        var fake = Fixtures.Tools(branchExists: false);
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"));
        var target = Fixtures.Flux(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => r.Success);
        fake.Calls.ShouldContain(c => c.FileName == "git" && c.Args.Contains("--orphan"));
        fake.Calls.ShouldNotContain(c => c.FileName == "git" && c.Args.Contains("reset"));

        // Flux: the GitRepository goes first, then the Kustomizations.
        var applied = fake.Calls.Where(c => c.FileName == "kubectl" && c.Args.Contains("apply")).Select(c => Path.GetFileName(c.Args[^1])).ToList();
        applied.ShouldBe([GitOpsTree.EnvironmentFile, "api.yaml", "worker.yaml"]);
    }

    [Fact]
    public async Task Apply_WithNothingToCommit_DoesNotPush()
    {
        var fake = Fixtures.Tools(changes: false);
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => r.Success && r.DeploymentId == "commit/0123456");
        fake.Calls.ShouldNotContain(c => c.Args.Contains("commit") || c.Args.Contains("push"));
    }

    [Fact]
    public async Task Apply_WithPushAndRegisterOff_OnlyCommits()
    {
        var fake = Fixtures.Tools();
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"),
            gitops: new { repoUrl = Fixtures.RepoUrl, checkoutDirectory = dir.File("checkout"), push = false, register = false });
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => r.Success);
        fake.Calls.ShouldContain(c => c.Args.Contains("commit"));
        fake.Calls.ShouldNotContain(c => c.Args.Contains("push"));
        fake.Calls.ShouldNotContain(c => c.FileName == "kubectl");
    }

    [Fact]
    public async Task Apply_WithoutPlan_AsksForPlanFirst()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"));

        var results = await Fixtures.Flux(Fixtures.Tools()).ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("deployify plan"));
    }

    [Fact]
    public async Task Apply_WithoutTheController_FailsWithAHint()
    {
        var fake = Fixtures.Tools().When(s => s.FileName == "kubectl" && s.Args.Contains("crd"), _ => new ProcessResult(0, "", ""));
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("Argo CD is not installed") && r.Error.Contains("gitops.register"));
    }

    [Fact]
    public async Task Apply_WithoutGit_FailsEveryService_WithTheReason()
    {
        var fake = Fixtures.Tools();
        fake.Missing.Add("git");
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("needs git"));
    }

    [Fact]
    public async Task ARejectedPush_SaysToRunApplyAgain()
    {
        var fake = Fixtures.Tools().When(["push"], new ProcessResult(1, "", " ! [rejected] HEAD -> main (fetch first)"));
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"));
        var target = Fixtures.Flux(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("Run apply again"));
    }

    // ── wait ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Wait_ReportsEachService_AsArgoCdSeesIt()
    {
        var fake = Fixtures.Tools()
            .When(s => s.Args.Contains("get") && s.Args.Contains("test-api"), _ => new ProcessResult(0, Application("Synced", "Healthy"), ""))
            .When(s => s.Args.Contains("get") && s.Args.Contains("test-worker"), _ => new ProcessResult(0, Application("Synced", "Degraded"), ""));
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"),
            gitops: new { repoUrl = Fixtures.RepoUrl, checkoutDirectory = dir.File("checkout"), wait = true, waitTimeoutSeconds = 5 });
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.Single(r => r.ServiceName == "api").Success.ShouldBeTrue();
        var worker = results.Single(r => r.ServiceName == "worker");
        worker.Success.ShouldBeFalse();
        worker.Error!.ShouldContain("degraded");
    }

    [Fact]
    public async Task Wait_GivesUp_AfterTheTimeout()
    {
        var fake = Fixtures.Tools()
            .When(s => s.Args.Contains("get") && s.Args.Contains("-o"), _ => new ProcessResult(0, Kustomization("main@sha1:fedcba9876543210", "True", "ReconciliationSucceeded"), ""));
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"),
            gitops: new { repoUrl = Fixtures.RepoUrl, checkoutDirectory = dir.File("checkout"), wait = true, waitTimeoutSeconds = 1 });
        var target = Fixtures.Flux(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var results = await target.ApplyAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        results.ShouldAllBe(r => !r.Success && r.Error!.Contains("did not finish") && r.Error.Contains("fedcba9"));
    }

    // ── plan / diff ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_ComparesWithWhatIsCommitted()
    {
        var fake = Fixtures.Tools();
        using var dir = new TempDir();
        var checkout = dir.File("checkout");
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, checkout);
        var target = Fixtures.Argo(fake);

        // Committed: api exactly as generated, worker with an edited deployment, and "old".
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);
        var environment = Path.Combine(checkout, "deployify", "test");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        await GitOpsTree.CopyIntoAsync(GitOpsTree.TreeDirectory(dir.Path), environment, ["api", "worker"], Ct);
        File.AppendAllText(Path.Combine(environment, "worker", "deployment.yaml"), "# edited\n");
        Directory.CreateDirectory(Path.Combine(environment, "old"));
        File.WriteAllText(Path.Combine(environment, "old", "kustomization.yaml"), "resources: []\n");

        var plan = await target.PlanAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        plan.Unchanged.ShouldBe(["api"]);
        plan.ToUpdate.ShouldBe(["worker"]);
        plan.ToDelete.ShouldBe(["old"]);
        fake.Calls.ShouldNotContain(c => c.Args.Contains("clone"));
    }

    [Fact]
    public async Task Diff_WhenTheRepositoryCantBeRead_IsUnknown()
    {
        var fake = Fixtures.Tools().When(["fetch"], new ProcessResult(128, "", "fatal: Authentication failed"));
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"));

        var diff = await Fixtures.Flux(fake).DiffAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        diff.ShouldAllBe(d => d.Kind == DiffKind.Unknown && d.Reason!.Contains("Authentication failed"));
    }

    [Fact]
    public async Task Diff_OnANewBranch_AddsEveryService()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"));

        var plan = await Fixtures.Flux(Fixtures.Tools(branchExists: false)).PlanAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        plan.ToCreate.ShouldBe(["api", "worker"], ignoreOrder: true);
    }

    // ── destroy / prune ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteService_RemovesTheApplicationFirst_ThenTheFolder()
    {
        var fake = Fixtures.Tools();
        using var dir = new TempDir();
        var checkout = dir.File("checkout");
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, checkout);
        var target = Fixtures.Argo(fake);
        await target.GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);
        var environment = Path.Combine(checkout, "deployify", "test");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        await GitOpsTree.CopyIntoAsync(GitOpsTree.TreeDirectory(dir.Path), environment, ["api", "worker"], Ct);

        (await target.ListManagedAsync(config, Ct)).Select(m => m.Name).ShouldBe(["api", "worker"]);

        await target.DeleteServiceAsync(config, "worker", new DeleteOptions(), Ct);

        var delete = fake.Calls.Single(c => c.FileName == "kubectl" && c.Args.Contains("delete"));
        delete.Args.ShouldContain(ArgoCdManifests.Resource);
        delete.Args.ShouldContain("test-worker");
        delete.Args.ShouldContain("argocd");
        fake.Calls.IndexOf(delete).ShouldBeLessThan(fake.Calls.IndexOf(Git(fake, "commit")));
        Directory.Exists(Path.Combine(environment, "worker")).ShouldBeFalse();
        File.Exists(GitOpsTree.RegistrationFile(environment, "worker")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(environment, "kustomization.yaml")).ShouldNotContain("worker");
        Git(fake, "push").ShouldNotBeNull();
    }

    // ── status ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Synced", "Healthy", null, HealthState.Healthy)]
    [InlineData("OutOfSync", "Healthy", null, HealthState.Progressing)]
    [InlineData("Synced", "Progressing", null, HealthState.Progressing)]
    [InlineData("Synced", "Degraded", null, HealthState.Failed)]
    [InlineData("OutOfSync", "Missing", "Failed", HealthState.Failed)]
    public void ArgoCd_Status(string sync, string health, string? phase, HealthState expected)
        => ArgoCdManifests.Evaluate(Application(sync, health, phase), Fixtures.Commit).State.ShouldBe(expected);

    [Fact]
    public void ArgoCd_AtAnOlderCommit_IsStillProgressing()
        => ArgoCdManifests.Evaluate(Application("Synced", "Healthy", revision: "fedcba9876543210"), Fixtures.Commit)
            .State.ShouldBe(HealthState.Progressing);

    [Theory]
    [InlineData("main@sha1:0123456789abcdef0123456789abcdef01234567", "True", "ReconciliationSucceeded", HealthState.Healthy)]
    [InlineData("main/0123456789abcdef0123456789abcdef01234567", "True", "ReconciliationSucceeded", HealthState.Healthy)]
    [InlineData("main@sha1:fedcba9876543210", "True", "ReconciliationSucceeded", HealthState.Progressing)]
    [InlineData("main@sha1:0123456789abcdef0123456789abcdef01234567", "Unknown", "Progressing", HealthState.Progressing)]
    [InlineData("main@sha1:fedcba9876543210", "False", "HealthCheckFailed", HealthState.Failed)]
    public void Flux_Status(string applied, string ready, string reason, HealthState expected)
        => FluxManifests.Evaluate(Kustomization(applied, ready, reason), Fixtures.Commit).State.ShouldBe(expected);

    [Fact]
    public void AMissingObject_IsProgressing()
    {
        ArgoCdManifests.Evaluate(null, Fixtures.Commit).State.ShouldBe(HealthState.Progressing);
        FluxManifests.Evaluate(null, Fixtures.Commit).State.ShouldBe(HealthState.Progressing);
    }

    private static string Application(string sync, string health, string? phase = null, string revision = Fixtures.Commit)
        => $$"""
            {
              "status": {
                "sync": { "status": "{{sync}}", "revision": "{{revision}}" },
                "health": { "status": "{{health}}", "message": "pods crash" },
                "operationState": { "phase": "{{phase ?? "Succeeded"}}", "message": "boom", "syncResult": { "revision": "{{revision}}" } }
              }
            }
            """;

    /// <summary>A Kustomization that last applied <paramref name="applied"/> and is attempting the test commit.</summary>
    private static string Kustomization(string applied, string ready, string reason)
        => $$"""
            {
              "status": {
                "lastAppliedRevision": "{{applied}}",
                "lastAttemptedRevision": "main@sha1:{{Fixtures.Commit}}",
                "conditions": [ { "type": "Ready", "status": "{{ready}}", "reason": "{{reason}}", "message": "details" } ]
              }
            }
            """;
}
