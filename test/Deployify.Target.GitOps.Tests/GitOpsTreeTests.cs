using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Target.GitOps.Tests;

/// <summary>What generate writes for Argo CD and Flux: the same service folders, different registrations.</summary>
public sealed class GitOpsTreeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generate_WritesAFolderPerService_WithAKustomizationListingItsManifests()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));

        await Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var kustomization = dir.Read("gitops", "tree", "api", "kustomization.yaml");
        kustomization.ShouldContain("kind: Kustomization");
        kustomization.ShouldContain("  - deployment.yaml");
        kustomization.ShouldContain("  - namespace.yaml");
        File.Exists(dir.File("gitops", "tree", "api", "deployment.yaml")).ShouldBeTrue();

        var root = dir.Read("gitops", "tree", "kustomization.yaml");
        root.ShouldContain("  - api");
        root.ShouldContain("  - worker");
        root.ShouldNotContain(GitOpsTree.RegistrationFolder);
    }

    [Fact]
    public async Task ArgoCd_RegistersEachServiceAsAnApplication_PointingAtItsFolder()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"),
            controller: new { @namespace = "gitops", project = "shop", autoSync = true, selfHeal = false });

        await Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var app = dir.Read("gitops", "tree", "_registration", "api.yaml");
        app.ShouldContain("kind: Application");
        app.ShouldContain("  name: test-api");
        app.ShouldContain("  namespace: gitops");
        app.ShouldContain("resources-finalizer.argocd.argoproj.io");
        app.ShouldContain("    repoURL: \"https://git.example.com/platform/config.git\"");
        app.ShouldContain("    path: \"deployify/test/api\"");
        app.ShouldContain("    namespace: test-api");
        app.ShouldContain("      selfHeal: false");
        app.ShouldContain("  project: \"shop\"");
        File.Exists(dir.File("gitops", "tree", "_registration", GitOpsTree.EnvironmentFile)).ShouldBeFalse();
    }

    [Fact]
    public async Task ArgoCd_WithoutAutoSync_HasNoAutomatedPolicy()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"), controller: new { autoSync = false });

        await Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        dir.Read("gitops", "tree", "_registration", "api.yaml").ShouldNotContain("automated:");
    }

    [Fact]
    public async Task Flux_WritesAGitRepository_AndAKustomizationPerService()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"),
            gitops: new { repoUrl = Fixtures.RepoUrl, checkoutDirectory = dir.File("checkout"), path = "/clusters/prod/", branch = "release" },
            controller: new { secretRef = "repo-auth", interval = "2m" });

        await Fixtures.Flux(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        var source = dir.Read("gitops", "tree", "_registration", GitOpsTree.EnvironmentFile);
        source.ShouldContain("kind: GitRepository");
        source.ShouldContain("  name: test");
        source.ShouldContain("  namespace: flux-system");
        source.ShouldContain("    branch: \"release\"");
        source.ShouldContain("  secretRef:\n    name: repo-auth");

        var kustomization = dir.Read("gitops", "tree", "_registration", "worker.yaml");
        kustomization.ShouldContain("kind: Kustomization");
        kustomization.ShouldContain("  name: test-worker");
        kustomization.ShouldContain("  path: \"./clusters/prod/worker\"");
        kustomization.ShouldContain("  interval: \"2m\"");
        kustomization.ShouldContain("  prune: true");
        kustomization.ShouldContain("    name: test");
    }

    [Fact]
    public async Task Flux_WithAnExistingSource_WritesNoGitRepository()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"),
            controller: new { createSource = false, sourceName = "flux-system" });

        await Fixtures.Flux(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct);

        File.Exists(dir.File("gitops", "tree", "_registration", GitOpsTree.EnvironmentFile)).ShouldBeFalse();
        dir.Read("gitops", "tree", "_registration", "api.yaml").ShouldContain("    name: flux-system");
    }

    [Fact]
    public async Task InlineSecrets_AreRefused_BecauseTheyWouldBeCommitted()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.ManifestWithSecret(), config, dir.Path, Ct));

        ex.Message.ShouldContain("pg");
        ex.Message.ShouldContain("secrets.backend");
    }

    [Fact]
    public async Task WithAnExternalBackend_NoSecretValueIsWritten()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.Flux, dir.File("checkout"), backend: SecretBackendType.KubernetesSecret);

        await Fixtures.Flux(new FakeProcessRunner()).GenerateAsync(Fixtures.ManifestWithSecret(), config, dir.Path, Ct);

        var files = Directory.GetFiles(dir.File("gitops", "tree"), "*", SearchOption.AllDirectories);
        files.ShouldNotBeEmpty();
        files.ShouldAllBe(f => !File.ReadAllText(f).Contains(Fixtures.Secret));
        dir.Read("gitops", "tree", "pg", "deployment.yaml").ShouldContain("pg-secrets");
    }

    [Fact]
    public async Task Generate_WithoutARepository_SaysWhatIsMissing()
    {
        using var dir = new TempDir();
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, dir.File("checkout"), gitops: new { branch = "main" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Fixtures.Argo(new FakeProcessRunner()).GenerateAsync(Fixtures.Manifest(), config, dir.Path, Ct));

        ex.Message.ShouldContain("repoUrl");
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("clusters/../../etc")]
    [InlineData("C:/repo")]
    [InlineData("a//b")]
    public void APathOutsideTheRepository_IsRejected(string path)
    {
        var config = Fixtures.Config(DeploymentTargetType.ArgoCd, "checkout", gitops: new { repoUrl = Fixtures.RepoUrl, path });

        Should.Throw<InvalidOperationException>(() => GitOpsDeploymentTarget.RepoPath(config)).Message.ShouldContain("gitops.path");
    }

    [Fact]
    public void ThePath_DefaultsToTheEnvironment_AndIsNormalized()
    {
        GitOpsDeploymentTarget.RepoPath(Fixtures.Config(DeploymentTargetType.Flux, "checkout")).ShouldBe("deployify/test");
        GitOpsDeploymentTarget.RepoPath(Fixtures.Config(DeploymentTargetType.Flux, "checkout",
            gitops: new { repoUrl = Fixtures.RepoUrl, path = "\\clusters\\prod\\" })).ShouldBe("clusters/prod");
    }

    [Fact]
    public void Compare_IgnoresLineEndings_AndListsChangedFiles()
    {
        using var dir = new TempDir();
        Write(dir.File("tree", "api", "a.yaml"), "x: 1\ny: 2\n");
        Write(dir.File("tree", "api", "b.yaml"), "b\n");
        Write(dir.File("tree", "_registration", "api.yaml"), "app\n");
        Write(dir.File("repo", "api", "a.yaml"), "x: 1\r\ny: 2\r\n");
        Write(dir.File("repo", "api", "old.yaml"), "gone\n");
        Write(dir.File("repo", "_registration", "api.yaml"), "app\r\n");

        GitOpsTree.Compare(dir.File("tree"), dir.File("repo"), "api").ShouldBe(["b.yaml", "old.yaml"]);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
