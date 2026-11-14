using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.State;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class PublishedImagesTests
{
    internal const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string OtherDigest = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly DateTimeOffset s_t0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_ThenTryGetDigest_ByRepositoryAndTag()
    {
        var images = new PublishedImages();

        images.Record("reg.example.com/app/api", "v2", Digest, s_t0);

        images.TryGetDigest("reg.example.com/app/api", "v2", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
        images.TryGetDigest("reg.example.com/app/api", "v3", out _).ShouldBeFalse();
        images.HasChanges.ShouldBeTrue();
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void Record_IgnoresValuesThatAreNotDigests(string value)
    {
        var images = new PublishedImages();

        images.Record("api", "v2", value, s_t0);

        images.All.ShouldBeEmpty();
        images.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Load_DoesNotCountAsAChange_AndDoesNotReplaceWhatThisRunRecorded()
    {
        var images = new PublishedImages();
        images.Record("api", "v2", Digest, s_t0);

        images.Load(new Dictionary<string, PublishedImage>
        {
            ["api:v2"] = new(OtherDigest, s_t0.AddDays(-1)),
            ["api:v1"] = new(OtherDigest, s_t0.AddDays(-2))
        });

        images.TryGetDigest("api", "v2", out var v2).ShouldBeTrue();
        v2.ShouldBe(Digest);
        images.TryGetDigest("api", "v1", out _).ShouldBeTrue();
        images.Recorded.Keys.ShouldBe(["api:v2"]);
    }

    [Fact]
    public void Trim_KeepsTheNewestEntries_PerRepository()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => KeyValuePair.Create($"reg:5000/api:v{i}", new PublishedImage(Digest, s_t0.AddMinutes(i))))
            .Append(KeyValuePair.Create("reg:5000/web:v0", new PublishedImage(Digest, s_t0)));

        var trimmed = PublishedImages.Trim(entries, perRepository: 2);

        trimmed.Keys.Order().ShouldBe(["reg:5000/api:v3", "reg:5000/api:v4", "reg:5000/web:v0"]);
    }

    // ── ServiceImage ────────────────────────────────────────────────────────

    private static readonly AspireResource s_project = new() { Type = "project.v0", Path = "Api/Api.csproj" };

    [Fact]
    public void ServiceImage_ForAProject_IsTheRegistryRepository()
    {
        var config = new EnvironmentConfig { ContainerRegistry = "reg.example.com/app/" };

        ServiceImage.For("api", s_project, config).ShouldBe("reg.example.com/app/api");
    }

    [Fact]
    public void ServiceImage_WithoutARegistry_IsTheLocalName()
        => ServiceImage.For("api", s_project, new EnvironmentConfig()).ShouldBe("api");

    [Fact]
    public void ServiceImage_ForAContainer_IsTheManifestImage()
        => ServiceImage.For("cache", new AspireResource { Type = "container.v0", Image = "redis:7" },
            new EnvironmentConfig { ContainerRegistry = "reg" }).ShouldBe("redis:7");

    [Fact]
    public void ServiceImage_PinsTheDigest_ForTheServicesOwnTagOverride()
    {
        var config = new EnvironmentConfig { ContainerRegistry = "reg", ImageTag = "v1" };
        config.ServiceOverrides["api"] = new ServiceOverride { ImageTag = "hotfix" };
        config.PublishedImages.Record("reg/api", "hotfix", Digest, s_t0);
        config.PublishedImages.Record("reg/api", "v1", OtherDigest, s_t0);

        ServiceImage.For("api", s_project, config).ShouldBe($"reg/api@{Digest}");
    }

    [Fact]
    public void ServiceImage_PinsDockerfileBuilds()
    {
        var config = new EnvironmentConfig { ContainerRegistry = "reg", ImageTag = "v1" };
        config.PublishedImages.Record("reg/frontend", "v1", Digest, s_t0);
        var frontend = new AspireResource { Type = "dockerfile.v0", Path = "/src/web/Dockerfile", Context = "/src/web" };

        ServiceImage.For("frontend", frontend, config).ShouldBe($"reg/frontend@{Digest}");
    }

    // ── Store ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_SavesWhatThisRunRecorded_AndALaterRunLoadsIt()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var images = new PublishedImages();
        images.Record("reg/api", "v2", Digest, s_t0);

        await PublishedImageStore.SaveAsync(new StateSession(dir.Path, "prod", null), images, ct);

        var later = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(new StateSession(dir.Path, "prod", null), later, ct);
        later.TryGetDigest("reg/api", "v2", out var digest).ShouldBeTrue();
        digest.ShouldBe(Digest);
        later.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Store_KeepsEntriesAnotherRunSaved_AndDoesNotRewriteLoadedOnes()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var first = new PublishedImages();
        first.Record("reg/web", "v1", Digest, s_t0);
        await PublishedImageStore.SaveAsync(new StateSession(dir.Path, "prod", null), first, ct);

        // This run loaded web:v1 before another run re-pushed it with a new digest.
        var mine = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(new StateSession(dir.Path, "prod", null), mine, ct);
        var other = new PublishedImages();
        other.Record("reg/web", "v1", OtherDigest, s_t0.AddMinutes(1));
        await PublishedImageStore.SaveAsync(new StateSession(dir.Path, "prod", null), other, ct);
        mine.Record("reg/api", "v2", Digest, s_t0.AddMinutes(2));
        await PublishedImageStore.SaveAsync(new StateSession(dir.Path, "prod", null), mine, ct);

        var result = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(new StateSession(dir.Path, "prod", null), result, ct);
        result.TryGetDigest("reg/web", "v1", out var web).ShouldBeTrue();
        web.ShouldBe(OtherDigest);
        result.TryGetDigest("reg/api", "v2", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Store_IsPerEnvironment()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        var images = new PublishedImages();
        images.Record("reg/api", "v2", Digest, s_t0);
        await PublishedImageStore.SaveAsync(new StateSession(dir.Path, "staging", null), images, ct);

        var prod = new PublishedImages();
        await PublishedImageStore.LoadIntoAsync(new StateSession(dir.Path, "prod", null), prod, ct);

        prod.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_WritesNothing_WhenNothingWasRecorded()
    {
        using var dir = new TempDir();
        var state = new StateSession(dir.Path, "prod", null);

        await PublishedImageStore.SaveAsync(state, new PublishedImages(), TestContext.Current.CancellationToken);

        (await state.Backend.ReadAsync(PublishedImageStore.Key(state), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void EnvironmentConfig_DoesNotWriteDigestsToTheStateFile()
    {
        var config = new EnvironmentConfig();
        config.PublishedImages.Record("reg/api", "v2", Digest, s_t0);

        JsonSerializer.Serialize(config).ShouldNotContain("sha256");
    }
}
