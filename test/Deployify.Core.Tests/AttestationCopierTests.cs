using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

/// <summary>
/// <c>promote</c> copies the cosign attestations of an image it moves to another registry: Sigstore
/// bundles (OCI referrers, cosign 3) with <c>cosign attach bundle</c>, the older <c>.att</c> tag
/// with the image copy tool. It skips what the target has and checks the target has them all after.
/// </summary>
public sealed class AttestationCopierTests
{
    private const string Digest = PublishedImagesTests.Digest;
    private const string Source = "staging.example.com/app/api";
    private const string Target = "prod.example.com/app/api";
    private const string SourceRef = Source + "@" + Digest;
    private const string TargetRef = Target + "@" + Digest;
    private const string AttTag = "sha256-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.att";

    private const string Bundle =
        """{"mediaType":"application/vnd.dev.sigstore.bundle.v0.3+json","verificationMaterial":{},"dsseEnvelope":{"payload":"c2JvbQ==","payloadType":"application/vnd.in-toto+json","signatures":[{"sig":"QUJD"}]}}""";

    private const string Legacy =
        """{"payloadType":"application/vnd.in-toto+json","payload":"b2xk","signatures":[{"keyid":"","sig":"REVG"}]}""";

    private static readonly ProcessResult s_ok = new(0, "", "");

    /// <summary>A runner whose registries hold the attestations in <paramref name="source"/> and <paramref name="target"/>; attaching or copying adds them to the target.</summary>
    private static (FakeProcessRunner Runner, List<string> Target) Registry(string[] source, string[] target)
    {
        var inTarget = target.ToList();
        var runner = new FakeProcessRunner()
            .When(s => s.FileName == "cosign" && s.Args.Take(2).SequenceEqual(["download", "attestation"]), s =>
                new ProcessResult(0, string.Join("\n", s.Args[2] == SourceRef ? source : inTarget) + "\n", ""))
            .When(s => s.FileName == "cosign" && s.Args.Take(2).SequenceEqual(["attach", "bundle"]), s =>
            {
                for (var i = 2; i < s.Args.Count - 1; i++)
                {
                    if (s.Args[i] == "--bundle")
                    {
                        inTarget.Add(File.ReadAllText(s.Args[i + 1]));
                    }
                }

                return s_ok;
            })
            .When(s => s.FileName == "crane" && s.Args[0] == "copy", _ =>
            {
                inTarget.AddRange(source.Where(a => !a.Contains("dsseEnvelope", StringComparison.Ordinal)));
                return s_ok;
            });
        return (runner, inTarget);
    }

    private static Task<AttestationCopyResult> CopyAsync(FakeProcessRunner runner)
        => new AttestationCopier(runner).CopyAsync(new ImageCopier(runner), ImageCopyTool.Crane, Source, Digest, Target,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task CopiesBundles_WithCosignAttachBundle_ToTheSameDigest()
    {
        var (runner, target) = Registry([Bundle], []);

        var result = await CopyAsync(runner);

        result.ShouldBe(new AttestationCopyResult(1, 1));
        var attach = runner.Calls.Single(c => c.Args.Contains("attach"));
        attach.Args.Count.ShouldBe(5);
        attach.Args.Take(3).ShouldBe(["attach", "bundle", "--bundle"]);
        attach.Args[^1].ShouldBe(TargetRef);
        target.ShouldBe([Bundle]);
        runner.Calls.ShouldNotContain(c => c.FileName == "crane");
    }

    [Fact]
    public async Task CopiesTheAttTag_ForAttestationsInTheOlderFormat()
    {
        var (runner, _) = Registry([Legacy], []);

        var result = await CopyAsync(runner);

        result.ShouldBe(new AttestationCopyResult(1, 1));
        runner.Single("manifest").Args.ShouldBe(["manifest", $"{Source}:{AttTag}"]);
        runner.Single("copy").Args.ShouldBe(["copy", $"{Source}:{AttTag}", $"{Target}:{AttTag}"]);
        runner.Calls.ShouldNotContain(c => c.Args.Contains("attach"));
    }

    [Fact]
    public async Task CopiesNothing_ThatTheTargetAlreadyHas()
    {
        // cosign attach adds a new referrer every time, so copying again would duplicate them.
        var (runner, _) = Registry([Bundle, Legacy], [Legacy, Bundle]);

        var result = await CopyAsync(runner);

        result.ShouldBe(new AttestationCopyResult(2, 0));
        runner.Calls.ShouldNotContain(c => c.Args.Contains("attach") || c.FileName == "crane");
    }

    [Fact]
    public async Task DoesNothing_WhenTheSourceHasNoAttestations()
    {
        var (runner, _) = Registry([], []);

        (await CopyAsync(runner)).ShouldBe(new AttestationCopyResult(0, 0));
        runner.Calls.ShouldHaveSingleItem().Args.ShouldBe(["download", "attestation", SourceRef]);
    }

    [Fact]
    public async Task Throws_WhenAnAttestationIsMissingAfterTheCopy()
    {
        var (runner, _) = Registry([Bundle], []);
        runner.When(["attach", "bundle"], s_ok); // the registry drops it

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CopyAsync(runner));

        ex.Message.ShouldContain("1 of 1");
    }

    [Fact]
    public async Task Throws_WhenCosignCantReadTheSource()
    {
        var (runner, _) = Registry([Bundle], []);
        runner.When(s => s.Args.Contains(SourceRef), _ => new ProcessResult(1, "", "UNAUTHORIZED"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CopyAsync(runner));

        ex.Message.ShouldContain("UNAUTHORIZED");
    }

    [Fact]
    public void Parse_ReadsBundlesAndEnvelopes_AndSkipsOtherLines()
    {
        var parsed = AttestationCopier.Parse($"{Bundle}\nnot json\n{Legacy}\n{{\"other\":1}}\n");

        parsed.Select(a => a.IsBundle).ShouldBe([true, false]);
        parsed[0].Key.ShouldBe("c2JvbQ==|QUJD");
        parsed[1].Key.ShouldBe("b2xk|REVG");
    }

    [Fact]
    public void AttestationTag_IsCosignsDigestTag()
        => AttestationCopier.AttestationTag(Digest).ShouldBe(AttTag);
}
