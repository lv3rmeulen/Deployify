using System;
using System.Collections.Generic;

// The documents `--output json` writes. Version 1 of the schema in
// docs/public/schemas/deployify-output-v1.json, described in docs/.../reference/json-output.md.
//
// These types are the contract, not the domain models they're built from: renaming a property
// in Deployify.Core mustn't change the JSON. Within schema version 1, properties are only
// added, never renamed, removed or retyped. A new property needs the schema and the docs
// updated too; JsonOutputSchemaTests fails until they are.
namespace Deployify.Commands.Json;

/// <summary>The envelope every command writes; <see cref="Result"/> holds the command's own document.</summary>
internal sealed record JsonEnvelope(
    int SchemaVersion,
    string Command,
    bool Success,
    int ExitCode,
    string? Error,
    string? Environment,
    JsonTarget? Target,
    IReadOnlyList<JsonDiagnostic> Diagnostics,
    object? Result);

internal sealed record JsonTarget(string Id, string Name);

internal enum JsonSeverity
{
    Info,
    Warning,
    Error
}

internal sealed record JsonDiagnostic(JsonSeverity Severity, string Message);

// ── plan ──────────────────────────────────────────────────────────────────────

internal sealed record JsonPlan(
    IReadOnlyList<string> Create,
    IReadOnlyList<string> Update,
    IReadOnlyList<string> Delete,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<JsonPlanUnknown> Unknown,
    IReadOnlyList<JsonSkippedResource> Skipped,
    bool UpToDate,
    bool DeletesOnApply,
    bool Blocked,
    string? BlockedReason,
    IReadOnlyList<JsonManifestFinding> ManifestFindings);

internal sealed record JsonPlanUnknown(string Service, string Reason);

internal sealed record JsonSkippedResource(string Name, string Type, string Reason);

internal sealed record JsonManifestFinding(string Resource, string? Type, JsonSeverity Severity, string Message);

// ── diff ──────────────────────────────────────────────────────────────────────

internal enum JsonDiffKind
{
    Added,
    Removed,
    Changed,
    Unchanged,
    Unknown
}

internal sealed record JsonDiff(bool Identical, IReadOnlyList<JsonDiffEntry> Entries);

/// <summary>
/// <see cref="OldValue"/> and <see cref="NewValue"/> are what the target reports, with every secret
/// value resolved during the run masked.
/// </summary>
internal sealed record JsonDiffEntry(
    string Service,
    JsonDiffKind Kind,
    string? Field,
    string? OldValue,
    string? NewValue,
    string? Reason);

// ── explain ───────────────────────────────────────────────────────────────────

internal sealed record JsonExplain(IReadOnlyList<JsonService> Services);

internal sealed record JsonService(
    string Name,
    string? Image,
    string? ImageTag,
    int Count,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, string> Details);

// ── status ────────────────────────────────────────────────────────────────────

/// <summary>Either <see cref="Rollouts"/> (the list) or <see cref="Rollout"/> (with <c>--rollout</c>) is set.</summary>
internal sealed record JsonStatus(IReadOnlyList<JsonRolloutSummary>? Rollouts, JsonRollout? Rollout);

internal sealed record JsonRolloutSummary(
    string Id,
    string Operation,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string State,
    string? Reason);

internal sealed record JsonRollout(
    string Id,
    string Operation,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string State,
    string? Reason,
    IReadOnlyList<IReadOnlyList<string>> Waves,
    IReadOnlyList<JsonRolloutService> Services,
    IReadOnlyList<JsonRolloutEvent> Events,
    string Journal);

internal sealed record JsonRolloutService(
    string Name,
    string State,
    JsonServiceSnapshot? Before,
    string? Revision,
    string? RestoredRevision,
    string? Detail,
    string? Strategy = null,
    string? Candidate = null,
    int? TrafficPercent = null,
    string? PreviewUrl = null);

internal sealed record JsonServiceSnapshot(bool Existed, string? Revision);

internal sealed record JsonRolloutEvent(DateTimeOffset At, string? Service, string Message);

// ── history ───────────────────────────────────────────────────────────────────

internal sealed record JsonHistory(IReadOnlyList<JsonRelease> Releases);

internal sealed record JsonRelease(
    string Id,
    string Kind,
    DateTimeOffset At,
    string Outcome,
    string? Tag,
    string? Commit,
    string? FromEnvironment,
    string? Actor,
    string? ActorSource,
    string? RunUrl,
    string? RolloutId,
    string? Summary,
    string? DeployifyVersion,
    IReadOnlyList<JsonReleaseImage> Images);

internal sealed record JsonReleaseImage(string Service, string Repository, string Tag, string? Digest);

// ── promote ───────────────────────────────────────────────────────────────────

internal sealed record JsonPromotion(
    string From,
    string To,
    string ImageTag,
    bool DryRun,
    bool Promoted,
    int Recorded,
    string? CopyTool,
    IReadOnlyList<JsonPromotedImage> Images);

internal sealed record JsonPromotedImage(
    string Service,
    string Repository,
    string Tag,
    string? Digest,
    string SourceRepository,
    string SourceTag,
    bool NeedsCopy,
    bool AlreadyPromoted,
    bool Copied,
    string? Problem,
    int? AttestationsCopied,
    int? SignaturesCopied,
    bool? SignatureVerified);

// ── drift ─────────────────────────────────────────────────────────────────────

/// <summary>The <c>result</c> of <c>drift --output json</c>: one entry per environment checked.</summary>
internal sealed record JsonDrift(bool Drifted, IReadOnlyList<JsonDriftEnvironment> Environments);

internal sealed record JsonDriftEnvironment(
    string Environment,
    JsonTarget? Target,
    string Status,
    DateTimeOffset CheckedAt,
    string? Reason,
    JsonDriftDeployed? Deployed,
    IReadOnlyList<JsonDriftEntry> Entries,
    int Ignored,
    string Fingerprint,
    JsonDriftAlert? Alert);

/// <summary>The deploy the live state was compared with.</summary>
internal sealed record JsonDriftDeployed(string? ReleaseId, DateTimeOffset At, string? Tag, string? Commit, string? Actor, string? RunUrl);

internal sealed record JsonDriftEntry(string Service, string Kind, string? Field, string? Live, string? Deployed, string? Reason);

/// <summary>The alert the check led to, and where it was delivered.</summary>
internal sealed record JsonDriftAlert(string Event, IReadOnlyList<JsonDriftDelivery> Deliveries);

internal sealed record JsonDriftDelivery(string Channel, string Type, bool Ok, string? Error);
