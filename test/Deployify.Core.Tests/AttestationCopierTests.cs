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

    // ── Signatures ────────────────────────────────────────────────────────────

    private const string SigTag = "sha256-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.sig";

    private const string SignatureBundle =
        """{"mediaType":"application/vnd.dev.sigstore.bundle.v0.3+json","verificationMaterial":{},"messageSignature":{"messageDigest":{"algorithm":"SHA2_256","digest":"ZGln"},"signature":"U0lH"}}""";

    private const string LegacySignature =
        """{"Base64Signature":"TEVHQUNZ","Payload":"cGF5bG9hZA==","Cert":null,"Chain":null,"Bundle":null}""";

    /// <summary>Like <see cref="Registry"/>, for <c>cosign download signature</c>; a source without signatures errors as cosign does.</summary>
    private static (FakeProcessRunner Runner, List<string> Target) SignatureRegistry(string[] source, string[] target)
    {
        var inTarget = target.ToList();
        var runner = new FakeProcessRunner()
            .When(s => s.FileName == "cosign" && s.Args.Take(2).SequenceEqual(["download", "signature"]), s =>
            {
                var found = s.Args[2] == SourceRef ? source : [.. inTarget];
                return found.Length == 0
                    ? new ProcessResult(1, "", $"Error: no signatures found\nmain.go:74: error during command execution: no signatures found")
                    : new ProcessResult(0, string.Join("\n", found) + "\n", "");
            })
            .When(s => s.FileName == "cosign" && s.Args.Take(2).SequenceEqual(["attach", "bundle"]), s =>
            {
                inTarget.Add(File.ReadAllText(s.Args[3]));
                return s_ok;
            })
            .When(s => s.FileName == "crane" && s.Args[0] == "copy", _ =>
            {
                inTarget.AddRange(source.Where(a => a.Contains("Base64Signature", StringComparison.Ordinal)));
                return s_ok;
            });
        return (runner, inTarget);
    }

    private static Task<AttestationCopyResult> CopySignaturesAsync(FakeProcessRunner runner)
        => new AttestationCopier(runner).CopyAsync(new ImageCopier(runner), ImageCopyTool.Crane, Source, Digest, Target,
            TestContext.Current.CancellationToken, SigstoreKind.Signature);

    [Fact]
    public async Task Signatures_CopiesBundles_ToTheSameDigest()
    {
        var (runner, target) = SignatureRegistry([SignatureBundle], []);

        var result = await CopySignaturesAsync(runner);

        result.ShouldBe(new AttestationCopyResult(1, 1));
        runner.Calls.First().Args.ShouldBe(["download", "signature", SourceRef]);
        runner.Calls.Single(c => c.Args.Contains("attach")).Args[^1].ShouldBe(TargetRef);
        target.ShouldBe([SignatureBundle]);
    }

    [Fact]
    public async Task Signatures_CopiesTheSigTag_ForTheOlderFormat()
    {
        var (runner, _) = SignatureRegistry([LegacySignature], []);

        var result = await CopySignaturesAsync(runner);

        result.ShouldBe(new AttestationCopyResult(1, 1));
        runner.Single("copy").Args.ShouldBe(["copy", $"{Source}:{SigTag}", $"{Target}:{SigTag}"]);
        runner.Calls.ShouldNotContain(c => c.Args.Contains("attach"));
    }

    [Fact]
    public async Task Signatures_CopiesNothing_ThatTheTargetAlreadyHas()
    {
        var (runner, _) = SignatureRegistry([SignatureBundle], [SignatureBundle]);

        (await CopySignaturesAsync(runner)).ShouldBe(new AttestationCopyResult(1, 0));
        runner.Calls.ShouldNotContain(c => c.Args.Contains("attach") || c.FileName == "crane");
    }

    [Fact]
    public async Task Signatures_NoneFound_IsNothingToCopy()
    {
        var (runner, _) = SignatureRegistry([], []);

        (await CopySignaturesAsync(runner)).ShouldBe(new AttestationCopyResult(0, 0));
    }

    [Fact]
    public void Parse_ReadsSignatureBundlesAndOlderSignatures()
    {
        var parsed = AttestationCopier.Parse($"{SignatureBundle}\n{LegacySignature}\n");

        parsed.Select(a => a.IsBundle).ShouldBe([true, false]);
        parsed[0].Key.ShouldBe("ZGln|U0lH");
        parsed[1].Key.ShouldBe("cGF5bG9hZA==|TEVHQUNZ");
    }

    [Fact]
    public void SignatureTag_IsCosignsDigestTag()
        => AttestationCopier.SignatureTag(Digest).ShouldBe(SigTag);
}
