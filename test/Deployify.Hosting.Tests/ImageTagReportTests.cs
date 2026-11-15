using System.Collections.Generic;
using Deployify.Core.Deployments;
using Deployify.Core.Services;
using Deployify.Hosting;
using Shouldly;
using Xunit;

namespace Deployify.Hosting.Tests;

/// <summary>
/// aspire deploy reports the image tag in its own settings' terms (Deployify:ImageTag,
/// Deployify:AllowMutableTag) rather than ci's flags.
/// </summary>
public class ImageTagReportTests
{
    private sealed class Recorder : IDeploymentReporter
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add("info: " + message);

        public void Warn(string message) => Lines.Add("warn: " + message);

        public void Error(string message) => Lines.Add("error: " + message);
    }

    [Fact]
    public void CommitSha_IsReportedWithItsSource()
    {
        var reporter = new Recorder();

        DeployifyDeployStep.ReportImageTag(
            new ImageTagDecision(ImageTagOutcome.CommitSha, "3f2a9c1", CommitSource: "GITHUB_SHA"), reporter).ShouldBeTrue();

        reporter.Lines.ShouldBe(["info: Image tag: 3f2a9c1 (commit SHA from GITHUB_SHA)"]);
    }

    [Fact]
    public void NoCommitSha_Stops_AndNamesTheAspireSettings()
    {
        var reporter = new Recorder();

        DeployifyDeployStep.ReportImageTag(
            new ImageTagDecision(ImageTagOutcome.NoCommitSha, "latest", Reason: "git is not available"), reporter).ShouldBeFalse();

        var line = reporter.Lines.ShouldHaveSingleItem();
        line.ShouldStartWith("error: ");
        line.ShouldContain("git is not available");
        line.ShouldContain("Deployify__ImageTag");
        line.ShouldContain("Deployify:AllowMutableTag=true");
        line.ShouldNotContain("--");
    }

    [Fact]
    public void ExplicitLatest_Stops_UnlessMutableTagsAreAllowed()
    {
        var refused = new Recorder();
        DeployifyDeployStep.ReportImageTag(
            new ImageTagDecision(ImageTagOutcome.MutableOverrideRefused, "latest", FromOverride: true), refused).ShouldBeFalse();
        refused.Lines.ShouldHaveSingleItem().ShouldContain("Deployify:ImageTag latest is overwritten by every build");

        var allowed = new Recorder();
        DeployifyDeployStep.ReportImageTag(
            new ImageTagDecision(ImageTagOutcome.MutableAllowed, "latest", FromOverride: true), allowed).ShouldBeTrue();
        allowed.Lines.ShouldBe(["warn: Publishing with the mutable tag latest: a rollback can't restore the previous image."]);
    }

    [Fact]
    public void NotPublishing_ReportsNothing()
    {
        var reporter = new Recorder();

        DeployifyDeployStep.ReportImageTag(new ImageTagDecision(ImageTagOutcome.NotPublishing, "latest"), reporter).ShouldBeTrue();

        reporter.Lines.ShouldBeEmpty();
    }
}
