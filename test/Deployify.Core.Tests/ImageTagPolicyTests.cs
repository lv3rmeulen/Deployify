using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

/// <summary>
/// The tag rule ci and aspire deploy share: an explicit tag wins; a run that publishes replaces
/// "latest" with the commit SHA; with no SHA it stops unless mutable tags are allowed.
/// </summary>
public class ImageTagPolicyTests
{
    private const string Sha = "3f2a9c1e5b7d4e8a9c217d4e5f6a8b90c1d2e3f4";

    private static Func<string, string?> Env(string? sha)
        => name => name == "GITHUB_SHA" ? sha : null;

    private static FakeProcessRunner NoGit()
        => new FakeProcessRunner().When(["rev-parse", "HEAD"], new ProcessResult(128, "", "fatal: not a git repository"));

    private static Task<ImageTagDecision> Apply(
        EnvironmentConfig config, string? tagOverride, bool publishes, bool allowMutable, string? sha)
        => ImageTagPolicy.ApplyAsync(config, tagOverride, publishes, allowMutable, "/repo",
            Env(sha), NoGit(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Publishing_ReplacesLatest_WithTheCommitSha()
    {
        var config = new EnvironmentConfig { ImageTag = "latest" };

        var decision = await Apply(config, null, publishes: true, allowMutable: false, Sha);

        decision.ShouldBe(new ImageTagDecision(ImageTagOutcome.CommitSha, Sha, CommitSource: "GITHUB_SHA"));
        decision.Succeeded.ShouldBeTrue();
        config.ImageTag.ShouldBe(Sha);
    }

    [Fact]
    public async Task Publishing_KeepsAnImmutableTag()
    {
        var config = new EnvironmentConfig { ImageTag = "1.4.2" };

        (await Apply(config, null, publishes: true, allowMutable: false, Sha)).Outcome.ShouldBe(ImageTagOutcome.Immutable);
        config.ImageTag.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task Override_WinsOverTheCommitSha_AndIsTrimmed()
    {
        var config = new EnvironmentConfig { ImageTag = "latest" };

        var decision = await Apply(config, " release-42 ", publishes: true, allowMutable: false, Sha);

        decision.ShouldBe(new ImageTagDecision(ImageTagOutcome.Immutable, "release-42", FromOverride: true));
        config.ImageTag.ShouldBe("release-42");
    }

    [Fact]
    public async Task NotPublishing_KeepsLatest_ButAppliesAnOverride()
    {
        var kept = new EnvironmentConfig { ImageTag = "latest" };
        (await Apply(kept, null, publishes: false, allowMutable: false, Sha)).Outcome.ShouldBe(ImageTagOutcome.NotPublishing);
        kept.ImageTag.ShouldBe("latest");

        var overridden = new EnvironmentConfig { ImageTag = "latest" };
        (await Apply(overridden, Sha, publishes: false, allowMutable: false, null)).Outcome.ShouldBe(ImageTagOutcome.NotPublishing);
        overridden.ImageTag.ShouldBe(Sha);
    }

    [Theory]
    [InlineData(false, ImageTagOutcome.NoCommitSha)]
    [InlineData(true, ImageTagOutcome.MutableAllowed)]
    public async Task Publishing_WithoutACommitSha_StopsUnlessMutableTagsAreAllowed(bool allowMutable, ImageTagOutcome expected)
    {
        var config = new EnvironmentConfig { ImageTag = "latest" };

        var decision = await Apply(config, null, publishes: true, allowMutable, sha: null);

        decision.Outcome.ShouldBe(expected);
        decision.Succeeded.ShouldBe(allowMutable);
        decision.Reason.ShouldNotBeNullOrEmpty();
        config.ImageTag.ShouldBe("latest");
    }

    [Theory]
    [InlineData(false, ImageTagOutcome.MutableOverrideRefused)]
    [InlineData(true, ImageTagOutcome.MutableAllowed)]
    public async Task ExplicitLatest_IsNotReplacedByTheSha(bool allowMutable, ImageTagOutcome expected)
    {
        var config = new EnvironmentConfig { ImageTag = "1.0" };

        var decision = await Apply(config, "latest", publishes: true, allowMutable, Sha);

        decision.ShouldBe(new ImageTagDecision(expected, "latest", FromOverride: true));
        config.ImageTag.ShouldBe("latest");
    }
}
