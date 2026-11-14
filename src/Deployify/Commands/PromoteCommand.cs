using System;
using System.CommandLine;
using System.Linq;
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
/// registry, the images are copied there by digest first. Nothing is deployed.
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
        cmd.Options.Add(fromOpt);
        cmd.Options.Add(toOpt);
        cmd.Options.Add(imageTagOpt);
        cmd.Options.Add(copyWithOpt);
        cmd.Options.Add(GlobalOptions.DryRun);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var projectPath = parseResult.GetValue(GlobalOptions.ProjectPath)!;
            var from = parseResult.GetValue(fromOpt)!;
            var to = parseResult.GetValue(toOpt)!;
            var tagOverride = parseResult.GetValue(imageTagOpt)?.Trim();
            var dryRun = parseResult.GetValue(GlobalOptions.DryRun);
            ImageCopyTool? copyWith = ImageCopier.TryParse(parseResult.GetValue(copyWithOpt), out var parsedTool)
                ? parsedTool
                : null;

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[red]✗[/] --from and --to name the same environment.");
                return 1;
            }

            // The target is locked: promoting changes what its next apply deploys.
            ResolvedContext resolved;
            try
            {
                resolved = await DependencyInjection.ResolveAsync(projectPath, to, null, ct,
                    lockFor: dryRun ? null : "promote");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
                return 1;
            }

            await using var _ = resolved;
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
                AnsiConsole.MarkupLineInterpolated($"[red]✗[/] Could not read the published images of {from}: {ex.Message}");
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
                AnsiConsole.MarkupLineInterpolated(
                    $"\n[red]✗[/] {problems} image(s) can't be promoted, so nothing was promoted.");
                return 1;
            }

            var copies = entries.Where(e => e is { NeedsCopy: true, AlreadyPromoted: false }).ToList();
            var copier = new ImageCopier(logger: resolved.Services.GetService<ILoggerFactory>()?.CreateLogger("Deployify.Promote"));
            ImageCopyTool tool = default;
            if (copies.Count > 0)
            {
                if (await ResolveCopyToolAsync(copier, copyWith, ct) is not { } found)
                {
                    return 1;
                }

                tool = found;
                AnsiConsole.MarkupLineInterpolated(
                    $"\n[dim]{copies.Count} image(s) are copied to {to}'s registry with {tool.ToString().ToLowerInvariant()}.[/]");
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
                    AnsiConsole.MarkupLineInterpolated($"  [green]✓[/] {e.Service}: copied to {e.TargetRepository}:{e.TargetTag}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {e.Service}: {ex.Message}");
                    AnsiConsole.MarkupLine("Nothing was recorded. Fix the problem and promote again; images already copied are skipped.");
                    return 1;
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
                    AnsiConsole.MarkupLineInterpolated(
                        $"[red]✗[/] Could not record the digests in {to}'s state ({resolved.State.Backend.Description}): {ex.Message}");
                    return 1;
                }
            }

            AnsiConsole.MarkupLineInterpolated(
                $"\n[green]✓[/] Promoted {entries.Count} image(s) to [cyan]{to}[/] ({recorded} newly recorded).");
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Deploy them with: deployify apply --env {to} --image-tag {tag} --require-digest[/]");
            return 0;
        });

        return cmd;
    }

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

            AnsiConsole.MarkupLineInterpolated(
                $"[red]✗[/] --copy-with {tool.ToString().ToLowerInvariant()} is set, but it isn't installed or doesn't run.");
            return null;
        }

        if (await copier.DetectAsync(ct) is { } detected)
        {
            return detected;
        }

        AnsiConsole.MarkupLine("[red]✗[/] The target uses another registry, so the images have to be copied there, " +
            "but none of crane, skopeo or docker buildx is installed. Install one of them; they sign in with your " +
            "docker login credentials.");
        return null;
    }
}
