using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Deployify.Core.SecretBackends;
using Deployify.Core.Services;
using Deployify.Core.State;

namespace Deployify.Core.Drift;

/// <summary>How an environment's live state compares with its last deploy.</summary>
public enum DriftStatus
{
    /// <summary>Everything Deployify deployed is as it was deployed.</summary>
    InSync,

    /// <summary>Something was changed, removed or added outside Deployify.</summary>
    Drifted,

    /// <summary>Nothing drifted that could be checked, but some services couldn't be compared.</summary>
    Unknown,

    /// <summary>The check couldn't run (the target is unreachable, the baseline can't be read, …).</summary>
    Error,

    /// <summary>There is no successful deploy to compare against yet.</summary>
    NotDeployed,

    /// <summary>Not checked: a deploy holds the environment's lock, or drift checks are off for it.</summary>
    Skipped
}

/// <summary>How one service differs from what was deployed.</summary>
public enum DriftKind
{
    /// <summary>Deployed, but no longer on the target (deleted by hand, say).</summary>
    Missing,

    /// <summary>On the target, but not part of what was deployed.</summary>
    Extra,

    /// <summary>On the target, with settings that differ from what was deployed.</summary>
    Changed,

    /// <summary>The target couldn't compare this service.</summary>
    Unknown
}

/// <summary>One difference. <see cref="Live"/> and <see cref="Deployed"/> are as the target reports them, with secrets masked.</summary>
public sealed record DriftEntry(string Service, DriftKind Kind, string? Field, string? Live, string? Deployed, string? Reason);

/// <summary>The result of checking one environment.</summary>
public sealed record DriftCheck
{
    public required string Environment { get; init; }

    public required DriftStatus Status { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>The target's id and display name, when the environment could be resolved.</summary>
    public string? TargetId { get; init; }

    public string? TargetName { get; init; }

    /// <summary>The deploy that was compared against, without its manifest and settings.</summary>
    public DeployedBaseline? Baseline { get; init; }

    /// <summary>Every difference that isn't ignored, missing first.</summary>
    public IReadOnlyList<DriftEntry> Entries { get; init; } = [];

    /// <summary>Differences left out by <see cref="DriftConfig.Ignore"/>.</summary>
    public int Ignored { get; init; }

    /// <summary>Why the status is Error, Skipped or NotDeployed.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Identifies what drifted: the same differences give the same fingerprint, so an alert is sent
    /// when the drift appears or changes, not on every check. Empty when nothing drifted.
    /// </summary>
    public string Fingerprint => DriftDetector.Fingerprint(Entries);

    public int Count(DriftKind kind) => Entries.Count(e => e.Kind == kind);
}

/// <summary>
/// Compares an environment's live state with its last successful deploy: regenerates the
/// artifacts from the <see cref="DeployedBaseline"/> and asks the target for the differences
/// (what <c>deployify diff</c> shows, but against what was deployed rather than the working tree).
/// Read-only: nothing on the target or in state is changed.
/// </summary>
public static class DriftDetector
{
    /// <summary>Folder under the output path the baseline's artifacts are generated in, and removed after.</summary>
    public const string WorkDirectoryName = ".drift";

    public static async Task<DriftCheck> CheckAsync(DeploymentSession session, TimeProvider? time = null, CancellationToken ct = default)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var check = new DriftCheck
        {
            Environment = session.EnvName,
            Status = DriftStatus.Error,
            CheckedAt = now,
            TargetId = session.TargetType.Value,
            TargetName = session.Target.DisplayName
        };

        DeployedBaseline? baseline;
        try
        {
            baseline = await DeployedBaselineStore.ReadAsync(session.State, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return check with { Reason = $"Could not read what was deployed ({session.State.Backend.Description}): {ex.Message}" };
        }

        if (baseline is null)
        {
            return check with
            {
                Status = DriftStatus.NotDeployed,
                Reason = $"Nothing to compare against: {session.EnvName} has no successful deploy recorded in " +
                         $"{session.State.Backend.Description}. The next apply, ci or aspire deploy records one."
            };
        }

        check = check with { Baseline = baseline };

        try
        {
            if (await StateLease.PeekAsync(session.State.Backend, session.State.LockKey, time, ct) is { } holder)
            {
                return check with
                {
                    Status = DriftStatus.Skipped,
                    Reason = $"A deploy is in progress ({holder.Describe(now)}); checking now would report its changes as drift."
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return check with { Reason = $"Could not read the environment lock ({session.State.Backend.Description}): {ex.Message}" };
        }

        if (!string.Equals(baseline.Target, session.TargetType.Value, StringComparison.OrdinalIgnoreCase))
        {
            return check with
            {
                Reason = $"{session.EnvName} was last deployed to {baseline.Target} but now targets {session.TargetType.Value}. " +
                         "Deploy it to the new target to check it for drift."
            };
        }

        var ignore = session.Config.Drift?.Ignore ?? [];
        var workDirectory = Path.Combine(session.OutputDirectory, WorkDirectoryName, session.EnvName);
        try
        {
            var manifest = baseline.ToManifest();
            var config = baseline.ToConfig();
            var registry = session.Services.GetService(typeof(SecretRegistry)) as SecretRegistry;
            config.ParameterSource = ParameterSourceFor(session, config, registry);

            var (ok, error) = await session.Target.PingAsync(config, ct);
            if (!ok)
            {
                return check with { Reason = $"Could not reach {session.Target.DisplayName}: {error}" };
            }

            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }

            Directory.CreateDirectory(workDirectory);
            await session.Target.GenerateAsync(manifest, config, workDirectory, ct);
            var diff = await session.Target.DiffAsync(manifest, config, workDirectory, ct);

            var secrets = registry?.Snapshot() ?? new Dictionary<string, SensitiveValue>();
            var (entries, ignored) = Classify(diff, ignore, secrets);
            return check with
            {
                Status = Status(entries),
                Entries = entries,
                Ignored = ignored
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return check with { Reason = $"The drift check failed: {ex.Message}" };
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    /// <summary>
    /// The diff turned into drift: unchanged services dropped, ignored ones counted, the rest
    /// ordered missing, extra, changed, unknown, with every known secret masked.
    /// </summary>
    public static (IReadOnlyList<DriftEntry> Entries, int Ignored) Classify(
        IEnumerable<DiffEntry> diff, IReadOnlyCollection<string> ignore, IReadOnlyDictionary<string, SensitiveValue> secrets)
    {
        var entries = new List<DriftEntry>();
        var ignored = 0;
        foreach (var e in diff)
        {
            if (e.Kind == DiffKind.Unchanged)
            {
                continue;
            }

            if (e.Kind != DiffKind.Unknown && IsIgnored(e.ServiceName, e.Field, ignore))
            {
                ignored++;
                continue;
            }

            // Diff speaks from the working tree's side: "added" is what apply would create, which
            // for a drift check means it was deployed and is gone now.
            var kind = e.Kind switch
            {
                DiffKind.Added => DriftKind.Missing,
                DiffKind.Removed => DriftKind.Extra,
                DiffKind.Changed => DriftKind.Changed,
                _ => DriftKind.Unknown
            };
            entries.Add(new DriftEntry(e.ServiceName, kind, e.Field,
                SecretLeakScanner.Redact(e.OldValue, secrets),
                SecretLeakScanner.Redact(e.NewValue, secrets),
                SecretLeakScanner.Redact(e.Reason, secrets)));
        }

        return ([.. entries.OrderBy(e => e.Kind).ThenBy(e => e.Service, StringComparer.Ordinal)
            .ThenBy(e => e.Field, StringComparer.Ordinal)], ignored);
    }

    /// <summary>True when <paramref name="ignore"/> has <c>service</c>, <c>service:field</c>, or the same with <c>*</c> for the service.</summary>
    public static bool IsIgnored(string service, string? field, IEnumerable<string> ignore)
    {
        foreach (var rule in ignore)
        {
            var colon = rule.IndexOf(':');
            var ruleService = colon < 0 ? rule : rule[..colon];
            var ruleField = colon < 0 ? null : rule[(colon + 1)..];
            if (ruleService != "*" && !string.Equals(ruleService, service, StringComparison.Ordinal))
            {
                continue;
            }

            if (ruleField is null || string.Equals(ruleField, field, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static DriftStatus Status(IReadOnlyCollection<DriftEntry> entries)
        => entries.Any(e => e.Kind != DriftKind.Unknown) ? DriftStatus.Drifted
            : entries.Count > 0 ? DriftStatus.Unknown
            : DriftStatus.InSync;

    /// <summary>16 hex characters identifying the drifted entries (unknown ones don't count); empty when there are none.</summary>
    public static string Fingerprint(IEnumerable<DriftEntry> entries)
    {
        var lines = entries
            .Where(e => e.Kind != DriftKind.Unknown)
            .Select(e => string.Join('\u001f', e.Kind, e.Service, e.Field, e.Live, e.Deployed))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (lines.Count == 0)
        {
            return "";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', lines)));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// Parameter values for the deployed settings: their explicit values, then environment
    /// variables and defaults, then the generated values the deploy stored. Nothing is saved: the
    /// generated values are a copy, so a check never writes deployify-parameters.json or a secret store.
    /// </summary>
    private static IParameterSource ParameterSourceFor(DeploymentSession session, EnvironmentConfig config, SecretRegistry? registry)
    {
        var generated = session.GeneratedSecrets?.Values ?? ParameterStore.Load(session.Directory, session.EnvName).Values;
        var source = ParameterSources.Default(
            ParameterResolution.ExplicitValues(config),
            new Dictionary<string, string>(generated, StringComparer.Ordinal));
        return registry is null ? source : new RecordingParameterSource(source, registry);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
