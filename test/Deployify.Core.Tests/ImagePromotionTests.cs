using System;
using System.Collections.Generic;
using System.Linq;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class ImagePromotionTests
{
    private const string Digest = PublishedImagesTests.Digest;
    private const string OtherDigest = PublishedImagesTests.OtherDigest;
    private static readonly DateTimeOffset s_t0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static AspireManifest Manifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new() { Type = "project.v0", Path = "Api/Api.csproj" },
            ["web"] = new() { Type = "dockerfile.v0", Path = "/src/web/Dockerfile", Context = "/src/web" },
            ["cache"] = new() { Type = "container.v0", Image = "redis:7" }
        }
    };

    private static EnvironmentConfig Env(string registry = "reg.example.com/app", string tag = "abc123")
        => new() { ContainerRegistry = registry, ImageTag = tag };

    private static EnvironmentConfig PublishedStaging()
    {
        var staging = Env();
        staging.PublishedImages.Load(new Dictionary<string, PublishedImage>
        {
            ["reg.example.com/app/api:abc123"] = new(Digest, s_t0),
            ["reg.example.com/app/web:abc123"] = new(OtherDigest, s_t0)
        });
        return staging;
    }

    [Fact]
    public void Plan_SameRegistry_PromotesEveryBuiltImage_AndSkipsOtherContainers()
    {
        var entries = ImagePromotion.Plan(Manifest(), PublishedStaging(), "staging", Env());

        entries.Select(e => e.Service).ShouldBe(["api", "web"]);
        entries.ShouldAllBe(e => e.CanPromote && !e.AlreadyPromoted);
        entries.Single(e => e.Service == "api").Digest.ShouldBe(Digest);
        entries.Single(e => e.Service == "web").Digest.ShouldBe(OtherDigest);
    }

    [Fact]
    public void Record_PinsTheTargetsArtifacts_ToTheSourceDigests()
    {
        var prod = Env();
        var entries = ImagePromotion.Plan(Manifest(), PublishedStaging(), "staging", prod);

        ImagePromotion.Record(entries, prod, s_t0.AddHours(1)).ShouldBe(2);

        ServiceImage.For("api", Manifest().Resources["api"], prod).ShouldBe($"reg.example.com/app/api@{Digest}");
        prod.PublishedImages.Recorded.Count.ShouldBe(2);
        ImagePromotion.Unpinned(Manifest(), prod).ShouldBeEmpty();
    }

    [Fact]
    public void Plan_MissingDigest_IsAProblem()
    {
        var staging = Env();
        staging.PublishedImages.Load(new Dictionary<string, PublishedImage>
        {
            ["reg.example.com/app/api:abc123"] = new(Digest, s_t0)
        });

        var entries = ImagePromotion.Plan(Manifest(), staging, "staging", Env());

        var web = entries.Single(e => e.Service == "web");
        web.CanPromote.ShouldBeFalse();
        web.Problem!.ShouldContain("no published digest for reg.example.com/app/web:abc123");
    }

    [Fact]
    public void Plan_OtherTag_IsNotPromoted()
    {
        var staging = PublishedStaging();
        staging.ImageTag = "def456";

        ImagePromotion.Plan(Manifest(), staging, "staging", Env(tag: "def456"))
            .ShouldAllBe(e => !e.CanPromote);
    }

    [Fact]
    public void Plan_DifferentRegistry_IsNotSupportedYet()
    {
        var entries = ImagePromotion.Plan(Manifest(), PublishedStaging(), "staging", Env(registry: "prod.example.com/app"));

        entries.ShouldAllBe(e => !e.CanPromote);
        entries[0].Problem!.ShouldContain("different registry");
    }

    [Fact]
    public void Plan_NoSourceRegistry_ExplainsWhy()
    {
        var entries = ImagePromotion.Plan(Manifest(), Env(registry: ""), "local", Env());

        entries[0].Problem!.ShouldContain("no container registry");
    }

    [Fact]
    public void Plan_SameDigestAlreadyRecorded_IsAlreadyPromoted_AndNotRecordedAgain()
    {
        var prod = Env();
        prod.PublishedImages.Load(new Dictionary<string, PublishedImage>
        {
            ["reg.example.com/app/api:abc123"] = new(Digest, s_t0)
        });

        var entries = ImagePromotion.Plan(Manifest(), PublishedStaging(), "staging", prod);

        entries.Single(e => e.Service == "api").AlreadyPromoted.ShouldBeTrue();
        ImagePromotion.Record(entries, prod, s_t0).ShouldBe(1);
    }

    [Fact]
    public void Plan_UsesPerServiceTagOverrides()
    {
        var staging = PublishedStaging();
        staging.ServiceOverrides["web"] = new ServiceOverride { ImageTag = "pinned" };
        staging.PublishedImages.Load(new Dictionary<string, PublishedImage>
        {
            ["reg.example.com/app/web:pinned"] = new(Digest, s_t0)
        });

        var web = ImagePromotion.Plan(Manifest(), staging, "staging", Env()).Single(e => e.Service == "web");

        web.SourceTag.ShouldBe("pinned");
        web.TargetTag.ShouldBe("abc123");
        web.Digest.ShouldBe(Digest);
    }

    [Fact]
    public void Unpinned_ListsBuiltImagesWithoutADigest()
    {
        ImagePromotion.Unpinned(Manifest(), Env())
            .ShouldBe([("api", "reg.example.com/app/api:abc123"), ("web", "reg.example.com/app/web:abc123")]);
        ImagePromotion.Unpinned(Manifest(), PublishedStaging()).ShouldBeEmpty();
    }
}
