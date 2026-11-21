using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Deployify.Commands.Json;
using Deployify.Core.Deployments;
using Deployify.Core.Interfaces;
using Deployify.Core.Services;
using Deployify.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// <c>deployify promote --from staging --to prod</c>: records the image digests the source
/// environment published in the target environment's deployment state, so the target's next
/// apply deploys exactly those images without building them again. When the target uses another
/// registry, the images are copied there by digest first, with their cosign attestations when the
/// source environment attests SBOMs and their signatures when it signs them. With
/// <c>--require-signature</c>, every image must carry a signature the target trusts, checked in the
/// source registry before anything is copied and in the target registry before anything is
/// recorded. Nothing is deployed.
/// </summary>
public static class PromoteCommand
{
    public static Command Build()
    {
        var fromOpt = new Option<string>("--from")
        {
            Description = "Environment whose published images to promote (e.g. staging).",
            Required = true
        };
        var toOpt = new Option<string>("--to")
        {
            Description = "Environment to promote them to (e.g. prod).",
            Required = true
        };
        var imageTagOpt = new Option<string?>("--image-tag", "-i")
        {
            Description = "Tag to promote, for example the commit SHA. Defaults to the source environment's configured tag."
        };
        var copyWithOpt = new Option<string?>("--copy-with")
        {
            Description = "Tool that copies images when the target uses another registry: crane | skopeo | docker. " +
                          "Defaults to the first one installed, in that order."
        };
        copyWithOpt.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && !ImageCopier.TryParse(value, out _))
            {
                result.AddError($"--copy-with must be crane, skopeo or docker, not '{value}'.");
            }
        });

        var cmd = new Command("promote",
            "Make another environment deploy the images an environment already published, by digest, without rebuilding them.");
        cmd.Options.Add(GlobalOptions.ProjectPath);
        cmd.Options.Add(GlobalOptions.AllowUnverified);
        cmd.Options.Add(fromOpt);
        cmd.Options.Add(toOpt);
        cmd.Options.Add(imageTagOpt);
        cmd.Options.Add(copyWithOpt);
        cmd.Options.Add(GlobalOptions.RequireSignature);
        cmd.Options.Add(GlobalOptions.DryRun);
        cmd.Options.Add(GlobalOptions.Output);

        cmd.SetAction((parseResult, ct) => JsonOutput.RunAsync(parseResult, "promote", async json =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var from = parseResult.GetValue(fromOpt)!;
            var to = parseResult.GetValue(toOpt)!;
            var tagOverride = parseResult.GetValue(imageTagOpt)?.Trim();
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            var requireSignature = parseResult.GetValue(GlobalOptions.RequireSignature);
            ImageCopyTool? copyWith = ImageCopier.TryParse(parseResult.GetValue(copyWithOpt), out var parsedTool)
                ? parsedTool
                : null;

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                ConsoleDeploymentReporter.Instance.Error("--from and --to name the same environment.");
                return 1;
            }

            // The target is locked: promoting changes what its next apply deploys.
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, to, null, ct,
                    lockFor: dryRun ? null : "promote",
                    allowUnverifiedPlugins: parseResult.GetValue(GlobalOptions.AllowUnverified));
            }
            catch (Exception ex)
            {
                ConsoleDeploymentReporter.Instance.Error(ex.Message);
                return 1;
            }

            await using var _ = resolved;
            json?.Use(resolved);
            var target = resolved.Config;

            Core.Models.EnvironmentConfig source;
            try
            {
                var stateManager = resolved.Services.GetRequiredService<IStateManager>();
                source = (await stateManager.LoadActiveAsync(resolved.Directory, from, ct)).Config;
                await using var sourceState = new StateSession(resolved.Directory, from, source.State,
                    onWarning: ConsoleDeploymentReporter.Instance.Warn);
                await PublishedImageStore.LoadIntoAsync(sourceState, source.PublishedImages, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ConsoleDeploymentReporter.Instance.Error($"Could not read the published images of {from}: {ex.Message}");
                return 1;
            }

            var tag = string.IsNullOrEmpty(tagOverride) ? source.ImageTag : tagOverride;
            source.ImageTag = tag;
            target.ImageTag = tag;

            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Promoting [cyan]{from}[/] → [cyan]{to}[/], image tag [cyan]{tag}[/][/]\n");

            var manifest = await resolved.LoadManifestAsync(ct);
            if (manifest is null)
            {
                return 1;
            }

            var entries = ImagePromotion.Plan(manifest, source, from, target);
            var copied = new HashSet<string>(StringComparer.Ordinal);
            var attestationsCopied = new Dictionary<string, int>(StringComparer.Ordinal);
            var signaturesCopied = new Dictionary<string, int>(StringComparer.Ordinal);
            var signatureVerified = requireSignature
                ? entries.ToDictionary(e => e.Service, _ => false, StringComparer.Ordinal)
                : null;
            string? copyToolName = null;
            void SetResult(bool promoted, int recorded)
            {
                if (json is not null)
                {
                    json.Result = ToJson(from, to, tag, dryRun, promoted, recorded, copyToolName, entries, copied, attestationsCopied,
                        signaturesCopied, signatureVerified);
                }
            }

            SetResult(promoted: false, recorded: 0);
            if (entries.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]![/] The AppHost has no projects or Dockerfile builds; there is nothing to promote.");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Service").AddColumn("Image").AddColumn("Digest").AddColumn("Status");
            foreach (var e in entries)
            {
                var status = e.Problem is not null ? $"[red]✗ {Markup.Escape(e.Problem)}[/]"
                    : e.AlreadyPromoted ? "[dim]already promoted[/]"
                    : e.NeedsCopy ? $"[green]✓ copy from {Markup.Escape(e.SourceRepository)}, promote[/]"
                    : "[green]✓ promote[/]";
                table.AddRow(
                    Markup.Escape(e.Service),
                    Markup.Escape($"{e.TargetRepository}:{e.TargetTag}"),
                    Markup.Escape(e.Digest is { Length: > 19 } d ? d[..19] + "…" : e.Digest ?? "-"),
                    status);
            }

            AnsiConsole.Write(table);

            var problems = entries.Count(e => e.Problem is not null);
            if (problems > 0)
            {
                // All or nothing: a partly promoted environment would deploy a mix of builds.
                AnsiConsole.WriteLine();
                ConsoleDeploymentReporter.Instance.Error($"{problems} image(s) can't be promoted, so nothing was promoted.");
                return 1;
            }

            var copies = entries.Where(e => e is { NeedsCopy: true, AlreadyPromoted: false }).ToList();
            var logger = resolved.Services.GetService<ILoggerFactory>()?.CreateLogger("Deployify.Promote");
            var copier = new ImageCopier(logger: logger);

            // When the source attests SBOMs, the attestations go with every image that moves to another
            // repository, including ones promoted before: a second run fills in what a first one missed.
            var attestations = new AttestationCopier(logger: logger);
            var attested = source.Sbom is { Enabled: true, Attest: true }
                ? entries.Where(e => e.NeedsCopy).ToList()
                : [];
            if (attested.Count > 0 && !await attestations.IsAvailableAsync(ct))
            {
                ConsoleDeploymentReporter.Instance.Error(
                    $"{from} attests SBOMs (sbom.attest), so their attestations are copied with the images, but cosign " +
                    "isn't installed. Install cosign 3.0.3 or later.");
                return 1;
            }

            // Signatures go with every image that moves to another repository when the source signs
            // them, or when they're required, so the target can verify them where it pulls from.
            var signer = new ImageSigner(logger: logger);
            var signed = source.Signing.Enabled || requireSignature
                ? entries.Where(e => e.NeedsCopy).ToList()
                : [];
            SignaturePolicy? policy = null;
            var trustedBy = to;
            if (requireSignature)
            {
                // The target decides which signatures it trusts; one without signing settings trusts the source's.
                var trust = target.Signing;
                if (!ImageSigner.HasVerificationSettings(trust))
                {
                    trust = source.Signing;
                    trustedBy = from;
                }

                try
                {
                    policy = ImageSigner.ResolvePolicy(trust, resolved.AppHostDirectory, trustedBy);
                }
                catch (InvalidOperationException ex)
                {
                    ConsoleDeploymentReporter.Instance.Error($"{SignatureCheck.Flag} is set, but {ex.Message}");
                    return 1;
                }
            }

            if ((signed.Count > 0 || policy is not null) && !await signer.IsAvailableAsync(ct))
            {
                ConsoleDeploymentReporter.Instance.Error(policy is not null
                    ? $"{SignatureCheck.Flag} is set, but cosign isn't installed. Install cosign 3.0.3 or later."
                    : $"{from} signs its images (signing.enabled), so their signatures are copied with the images, but cosign " +
                      "isn't installed. Install cosign 3.0.3 or later.");
                return 1;
            }

            // Before copying anything, and in a dry-run too: it only reads the source registry.
            if (policy is not null)
            {
                AnsiConsole.WriteLine();
                var failures = await SignatureCheck.VerifyAsync(signer,
                    [.. entries.Select(e => (e.Service, $"{e.SourceRepository}@{e.Digest}"))], policy, ct);
                if (failures > 0)
                {
                    ConsoleDeploymentReporter.Instance.Error(
                        $"{failures} image(s) in {from} have no signature {trustedBy} trusts ({policy.Description}), so nothing was promoted.");
                    return 1;
                }

                // An image that stays in its repository is verified where the target pulls it; a copied one again after the copy.
                foreach (var e in entries)
                {
                    signatureVerified![e.Service] = !e.NeedsCopy;
                }

                SetResult(promoted: false, recorded: 0);
            }

            ImageCopyTool tool = default;
            if (copies.Count > 0 || attested.Count > 0 || signed.Count > 0)
            {
                if (await ResolveCopyToolAsync(copier, copyWith, ct) is not { } found)
                {
                    return 1;
                }

                tool = found;
                copyToolName = tool.ToString().ToLowerInvariant();
                SetResult(promoted: false, recorded: 0);
                if (copies.Count > 0)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"\n[dim]{copies.Count} image(s) are copied to {to}'s registry with {tool.ToString().ToLowerInvariant()}.[/]");
                }

                if (attested.Count > 0)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[dim]The attestations of {attested.Count} image(s) are copied to {to}'s registry with cosign.[/]");
                }

                if (signed.Count > 0)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[dim]The signatures of {signed.Count} image(s) are copied to {to}'s registry with cosign.[/]");
                }
            }

            if (dryRun)
            {
                AnsiConsole.MarkupLine("\n[yellow]Dry-run: nothing copied or recorded.[/]");
                return 0;
            }

            // Copy everything before recording anything: an image copied to a registry is harmless
            // on its own, but recorded digests the target can't pull would break its next deploy.
            foreach (var e in copies)
            {
                try
                {
                    if (await copier.ExistsAsync(tool, e.TargetRepository, e.Digest!, ct))
                    {
                        AnsiConsole.MarkupLineInterpolated($"  [dim]{e.Service}: already in {e.TargetRepository}[/]");
                        continue;
                    }

                    await ConsoleStatus.RunAsync($"Copying {e.Service}...",
                        () => copier.CopyAsync(tool, e.SourceRepository, e.Digest!, e.TargetRepository, e.TargetTag, ct));
                    copied.Add(e.Service);
                    SetResult(promoted: false, recorded: 0);
                    AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] {e.Service}: copied to {e.TargetRepository}:{e.TargetTag}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsoleDeploymentReporter.Instance.Error($"{e.Service}: {ex.Message}");
                    AnsiConsole.MarkupLine("Nothing was recorded. Fix the problem and promote again; images already copied are skipped.");
                    return 1;
                }
            }

            // Attestations after the images (cosign attaches them to the copied digest), and still before
            // recording: a target deploying images without their attestations would fail an admission policy.
            foreach (var e in attested)
            {
                try
                {
                    var result = await ConsoleStatus.RunAsync($"Copying {e.Service}'s attestations...",
                        () => attestations.CopyAsync(copier, tool, e.SourceRepository, e.Digest!, e.TargetRepository, ct));
                    attestationsCopied[e.Service] = result.Copied;
                    SetResult(promoted: false, recorded: 0);
                    if (result.Found == 0)
                    {
                        ConsoleDeploymentReporter.Instance.Warn(
                            $"{e.Service}: {e.SourceRepository}@{e.Digest} has no attestations to copy; it was published without them.");
                    }
                    else if (result.Copied == 0)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"  [dim]{e.Service}: {e.TargetRepository} already has its {result.Found} attestation(s)[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"  [green]✓[/] {e.Service}: copied {result.Copied} attestation(s) to {e.TargetRepository}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsoleDeploymentReporter.Instance.Error($"{e.Service}: {ex.Message}");
                    AnsiConsole.MarkupLine("Nothing was recorded. Fix the problem and promote again; images and attestations already copied are skipped.");
                    return 1;
                }
            }

            foreach (var e in signed)
            {
                try
                {
                    var result = await ConsoleStatus.RunAsync($"Copying {e.Service}'s signatures...",
                        () => attestations.CopyAsync(copier, tool, e.SourceRepository, e.Digest!, e.TargetRepository, ct,
                            SigstoreKind.Signature));
                    signaturesCopied[e.Service] = result.Copied;
                    SetResult(promoted: false, recorded: 0);
                    if (result.Found == 0)
                    {
                        ConsoleDeploymentReporter.Instance.Warn(
                            $"{e.Service}: {e.SourceRepository}@{e.Digest} has no signatures to copy; it was published without them.");
                    }
                    else if (result.Copied == 0)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"  [dim]{e.Service}: {e.TargetRepository} already has its {result.Found} signature(s)[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            $"  [green]✓[/] {e.Service}: copied {result.Copied} signature(s) to {e.TargetRepository}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsoleDeploymentReporter.Instance.Error($"{e.Service}: {ex.Message}");
                    AnsiConsole.MarkupLine("Nothing was recorded. Fix the problem and promote again; images and signatures already copied are skipped.");
                    return 1;
                }
            }

            // The copies must verify where the target pulls them from before the target is told to deploy them.
            if (policy is not null && signed.Count > 0)
            {
                var failures = await SignatureCheck.VerifyAsync(signer,
                    [.. signed.Select(e => (e.Service, $"{e.TargetRepository}@{e.Digest}"))], policy, ct);
                if (failures > 0)
                {
                    ConsoleDeploymentReporter.Instance.Error(
                        $"{failures} image(s) don't verify in {to}'s registry after copying their signatures, so nothing was recorded.");
                    return 1;
                }

                foreach (var e in signed)
                {
                    signatureVerified![e.Service] = true;
                }
            }

            var recorded = ImagePromotion.Record(entries, target, DateTimeOffset.UtcNow);
            if (recorded > 0)
            {
                try
                {
                    await PublishedImageStore.SaveAsync(resolved.State, target.PublishedImages, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsoleDeploymentReporter.Instance.Error(
                        $"Could not record the digests in {to}'s state ({resolved.State.Backend.Description}): {ex.Message}");
                    return 1;
                }
            }

            SetResult(promoted: true, recorded);
            AnsiConsole.MarkupLineInterpolated(
                $"\n[green]✓[/] Promoted {entries.Count} image(s) to [cyan]{to}[/] ({recorded} newly recorded).");
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Deploy them with: deployify apply --env {to} --image-tag {tag} {(requireSignature ? "--require-signature" : "--require-digest")}[/]");
            return 0;
        }));

        return cmd;
    }

    /// <summary>The <c>result</c> of <c>promote --output json</c>.</summary>
    internal static JsonPromotion ToJson(string from, string to, string tag, bool dryRun, bool promoted, int recorded,
        string? copyTool, IEnumerable<ImagePromotionEntry> entries, IReadOnlySet<string> copied,
        IReadOnlyDictionary<string, int>? attestationsCopied = null, IReadOnlyDictionary<string, int>? signaturesCopied = null,
        IReadOnlyDictionary<string, bool>? signatureVerified = null) => new(
        from, to, tag, dryRun, promoted, recorded, copyTool,
        [.. entries.Select(e => new JsonPromotedImage(e.Service, e.TargetRepository, e.TargetTag, e.Digest,
            e.SourceRepository, e.SourceTag, e.NeedsCopy, e.AlreadyPromoted, copied.Contains(e.Service), e.Problem,
            attestationsCopied is not null && attestationsCopied.TryGetValue(e.Service, out var n) ? n : null,
            signaturesCopied is not null && signaturesCopied.TryGetValue(e.Service, out var s) ? s : null,
            signatureVerified is not null && signatureVerified.TryGetValue(e.Service, out var v) ? v : null))]);

    /// <summary>The copy tool asked for (when installed), else the first one installed. Null after saying why.</summary>
    private static async System.Threading.Tasks.Task<ImageCopyTool?> ResolveCopyToolAsync(
        ImageCopier copier, ImageCopyTool? requested, System.Threading.CancellationToken ct)
    {
        if (requested is { } tool)
        {
            if (await copier.IsAvailableAsync(tool, ct))
            {
                return tool;
            }

            ConsoleDeploymentReporter.Instance.Error(
                $"--copy-with {tool.ToString().ToLowerInvariant()} is set, but it isn't installed or doesn't run.");
            return null;
        }

        if (await copier.DetectAsync(ct) is { } detected)
        {
            return detected;
        }

        ConsoleDeploymentReporter.Instance.Error("The target uses another registry, so the images have to be copied there, " +
            "but none of crane, skopeo or docker buildx is installed. Install one of them; they sign in with your " +
            "docker login credentials.");
        return null;
    }
}
