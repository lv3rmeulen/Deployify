using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class CommitImageTagTests
{
    private const string Sha = "3f2a9c1e5b7d4e8a9c217d4e5f6a8b90c1d2e3f4";
    private const string OtherSha = "aaaabbbbccccddddeeeeffff0000111122223333";

    private static System.Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    private static FakeProcessRunner Git(string head = Sha + "\n", int headExit = 0, string status = "")
        => new FakeProcessRunner()
            .When(["rev-parse", "HEAD"], new ProcessResult(headExit, head, headExit == 0 ? "" : "fatal: not a git repository"))
            .When(["status", "--porcelain"], new ProcessResult(0, status, ""));

    [Fact]
    public async Task UsesGitHubSha_WithoutRunningGit()
    {
        var runner = Git();

        var (tag, reason) = await CommitImageTag.ResolveAsync(
            "/repo", Env(("GITHUB_SHA", Sha)), runner, TestContext.Current.CancellationToken);

        tag.ShouldBe(new CommitTag(Sha, "GITHUB_SHA"));
        reason.ShouldBeNull();
        runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("BUILD_SOURCEVERSION")]
    [InlineData("CI_COMMIT_SHA")]
    [InlineData("BITBUCKET_COMMIT")]
    [InlineData("GIT_COMMIT")]
    public async Task UsesOtherCiVariables(string variable)
    {
        var (tag, _) = await CommitImageTag.ResolveAsync(
            "/repo", Env((variable, Sha)), Git(), TestContext.Current.CancellationToken);

        tag.ShouldBe(new CommitTag(Sha, variable));
    }

    [Fact]
    public async Task ChecksVariablesInOrder()
    {
        var (tag, _) = await CommitImageTag.ResolveAsync(
            "/repo", Env(("CI_COMMIT_SHA", OtherSha), ("GITHUB_SHA", Sha)), Git(), TestContext.Current.CancellationToken);

        tag!.Source.ShouldBe("GITHUB_SHA");
    }

    [Fact]
    public async Task IgnoresVariablesThatAreNotCommitShas_AndFallsBackToGit()
    {
        var (tag, _) = await CommitImageTag.ResolveAsync(
            "/repo", Env(("GITHUB_SHA", "refs/heads/main"), ("GIT_COMMIT", "")), Git(), TestContext.Current.CancellationToken);

        tag.ShouldBe(new CommitTag(Sha, "git"));
    }

    [Fact]
    public async Task LowercasesTheSha()
    {
        var (tag, _) = await CommitImageTag.ResolveAsync(
            "/repo", Env(("BUILD_SOURCEVERSION", Sha.ToUpperInvariant())), Git(), TestContext.Current.CancellationToken);

        tag!.Tag.ShouldBe(Sha);
    }

    [Fact]
    public async Task UsesGitHead_InTheGivenDirectory_WhenTheTreeIsClean()
    {
        var runner = Git();

        var (tag, _) = await CommitImageTag.ResolveAsync("/repo", Env(), runner, TestContext.Current.CancellationToken);

        tag.ShouldBe(new CommitTag(Sha, "git"));
        runner.Single("rev-parse", "HEAD").WorkingDirectory.ShouldBe("/repo");
        runner.Single("status", "--porcelain", "--untracked-files=no").WorkingDirectory.ShouldBe("/repo");
    }

    [Fact]
    public async Task RefusesGitHead_WhenTrackedFilesHaveUncommittedChanges()
    {
        var (tag, reason) = await CommitImageTag.ResolveAsync(
            "/repo", Env(), Git(status: " M src/Api/Program.cs\n"), TestContext.Current.CancellationToken);

        tag.ShouldBeNull();
        reason!.ShouldContain("uncommitted changes");
    }

    [Fact]
    public async Task ReturnsReason_OutsideAGitRepository()
    {
        var (tag, reason) = await CommitImageTag.ResolveAsync(
            "/repo", Env(), Git(head: "", headExit: 128), TestContext.Current.CancellationToken);

        tag.ShouldBeNull();
        reason!.ShouldContain("not a git repository");
    }

    [Fact]
    public async Task ReturnsReason_WhenGitIsNotInstalled()
    {
        var runner = Git();
        runner.Missing.Add("git");

        var (tag, reason) = await CommitImageTag.ResolveAsync("/repo", Env(), runner, TestContext.Current.CancellationToken);

        tag.ShouldBeNull();
        reason!.ShouldContain("git is not available");
    }

    [Theory]
    [InlineData("3f2a9c1", "3f2a9c1")]
    [InlineData(" 3F2A9C1E \n", "3f2a9c1e")]
    [InlineData("3f2a9c", null)]
    [InlineData("not-a-sha", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize(string? value, string? expected)
        => CommitImageTag.Normalize(value).ShouldBe(expected);
}
