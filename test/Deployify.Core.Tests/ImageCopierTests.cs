using System;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class ImageCopierTests
{
    private const string Digest = PublishedImagesTests.Digest;
    private const string Source = "staging.example.com/app/api";
    private const string Target = "prod.example.com/app/api";

    private static readonly ProcessResult s_ok = new(0, "", "");
    private static readonly ProcessResult s_fail = new(1, "", "MANIFEST_UNKNOWN");

    [Fact]
    public async Task Detect_PrefersCrane_ThenSkopeo_ThenDocker()
    {
        var runner = new FakeProcessRunner();
        var copier = new ImageCopier(runner);

        (await copier.DetectAsync()).ShouldBe(ImageCopyTool.Crane);

        runner.Missing.Add("crane");
        (await copier.DetectAsync()).ShouldBe(ImageCopyTool.Skopeo);

        runner.Missing.Add("skopeo");
        (await copier.DetectAsync()).ShouldBe(ImageCopyTool.Docker);

        runner.When(["buildx", "version"], s_fail);
        (await copier.DetectAsync()).ShouldBeNull();
    }

    [Theory]
    [InlineData(ImageCopyTool.Crane, "crane", new[] { "copy", Source + "@" + Digest, Target + ":abc123" })]
    [InlineData(ImageCopyTool.Skopeo, "skopeo",
        new[] { "copy", "--all", "--preserve-digests", "docker://" + Source + "@" + Digest, "docker://" + Target + ":abc123" })]
    [InlineData(ImageCopyTool.Docker, "docker",
        new[] { "buildx", "imagetools", "create", "--tag", Target + ":abc123", Source + "@" + Digest })]
    public async Task Copy_CopiesTheSourceDigest_ToTheTargetTag(ImageCopyTool tool, string program, string[] args)
    {
        var runner = new FakeProcessRunner();

        await new ImageCopier(runner).CopyAsync(tool, Source, Digest, Target, "abc123");

        var copy = runner.Calls.First();
        copy.FileName.ShouldBe(program);
        copy.Args.ShouldBe(args);
    }

    [Theory]
    [InlineData(ImageCopyTool.Crane, "manifest", Target + "@" + Digest)]
    [InlineData(ImageCopyTool.Skopeo, "inspect", "docker://" + Target + "@" + Digest)]
    [InlineData(ImageCopyTool.Docker, "inspect", Target + "@" + Digest)]
    public async Task Copy_ChecksTheTargetHasTheSameDigest(ImageCopyTool tool, string verb, string reference)
    {
        var runner = new FakeProcessRunner();

        await new ImageCopier(runner).CopyAsync(tool, Source, Digest, Target, "abc123");

        runner.Calls.Count.ShouldBe(2);
        runner.Calls[1].Args.ShouldContain(verb);
        runner.Calls[1].Args[^1].ShouldBe(reference);
    }

    [Fact]
    public async Task Copy_Fails_WhenTheToolFails()
    {
        var runner = new FakeProcessRunner().When(["copy"], new ProcessResult(1, "", "UNAUTHORIZED: authentication required"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new ImageCopier(runner).CopyAsync(ImageCopyTool.Crane, Source, Digest, Target, "abc123"));

        ex.Message.ShouldContain("UNAUTHORIZED");
        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Copy_Fails_WhenTheDigestChanged()
    {
        // docker buildx can rewrite a manifest; the target then has no manifest with the source digest.
        var runner = new FakeProcessRunner().When(["imagetools", "inspect"], s_fail);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new ImageCopier(runner).CopyAsync(ImageCopyTool.Docker, Source, Digest, Target, "abc123"));

        ex.Message.ShouldContain("changed the image");
    }

    [Fact]
    public async Task Exists_IsFalse_WhenTheRegistryHasNoSuchManifest()
    {
        var runner = new FakeProcessRunner().When(["manifest"], s_fail);

        (await new ImageCopier(runner).ExistsAsync(ImageCopyTool.Crane, Target, Digest)).ShouldBeFalse();
        (await new ImageCopier(new FakeProcessRunner()).ExistsAsync(ImageCopyTool.Crane, Target, Digest)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("crane", ImageCopyTool.Crane)]
    [InlineData(" Skopeo ", ImageCopyTool.Skopeo)]
    [InlineData("DOCKER", ImageCopyTool.Docker)]
    public void TryParse_AcceptsToolNames(string value, ImageCopyTool expected)
    {
        ImageCopier.TryParse(value, out var tool).ShouldBeTrue();
        tool.ShouldBe(expected);
    }

    [Theory]
    [InlineData("podman")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsOtherValues(string? value) => ImageCopier.TryParse(value, out _).ShouldBeFalse();
}
