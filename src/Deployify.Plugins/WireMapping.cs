using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;

namespace Deployify.Plugins;

/// <summary>
/// Converts between Deployify's own types and the plugin wire contract. Both sides use it:
/// Deployify to call a plugin, the SDK to hand a plugin target the types it implements against.
/// </summary>
public static class WireMapping
{
    /// <summary>The same settings deployify-state.json is written with, so the environment reads the same.</summary>
    private static readonly JsonSerializerOptions s_stateJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Context ──────────────────────────────────────────────────────────────

    public static JsonElement ToWire(AspireManifest manifest) => JsonSerializer.SerializeToElement(manifest, s_stateJson);

    public static AspireManifest ManifestFromWire(JsonElement manifest)
        => manifest.Deserialize<AspireManifest>(s_stateJson) ?? new AspireManifest();

    /// <summary>
    /// The environment as it appears in deployify-state.json, minus explicit parameter values.
    /// A plugin asks for the values it needs, one at a time, so Deployify can see and record
    /// every secret that leaves the process.
    /// </summary>
    public static JsonElement ToWire(EnvironmentConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, s_stateJson)!.AsObject();
        node.Remove(nameof(EnvironmentConfig.Parameters));
        return node.Deserialize<JsonElement>();
    }

    public static EnvironmentConfig EnvironmentFromWire(JsonElement environment)
        => environment.Deserialize<EnvironmentConfig>(s_stateJson) ?? new EnvironmentConfig();

    // ── Results ──────────────────────────────────────────────────────────────

    public static ArtifactValidation ToWire(ValidationResult r) => new(r.ArtifactName, [.. r.Errors]);

    public static ValidationResult FromWire(ArtifactValidation r) => new() { ArtifactName = r.ArtifactName, Errors = [.. r.Errors] };

    public static PlanInfo ToWire(PlanResult p) => new()
    {
        ToCreate = [.. p.ToCreate],
        ToUpdate = [.. p.ToUpdate],
        ToDelete = [.. p.ToDelete],
        Unchanged = [.. p.Unchanged],
        Unknown = [.. p.Unknown.Select(u => new PlanUnknownInfo(u.ServiceName, u.Reason))]
    };

    public static PlanResult FromWire(PlanInfo p) => new()
    {
        ToCreate = [.. p.ToCreate],
        ToUpdate = [.. p.ToUpdate],
        ToDelete = [.. p.ToDelete],
        Unchanged = [.. p.Unchanged],
        Unknown = [.. p.Unknown.Select(u => new PlanUnknown(u.ServiceName, u.Reason))]
    };

    public static ServiceDeploymentInfo ToWire(DeploymentResult r) => new()
    {
        ServiceName = r.ServiceName, DeploymentId = r.DeploymentId, Success = r.Success, Error = r.Error
    };

    public static DeploymentResult FromWire(ServiceDeploymentInfo r) => new()
    {
        ServiceName = r.ServiceName, DeploymentId = r.DeploymentId, Success = r.Success, Error = r.Error
    };

    public static ServiceExplanationInfo ToWire(ServiceExplanation e) => new()
    {
        Name = e.Name,
        Image = e.Image,
        ImageTag = e.ImageTag,
        Count = e.Count,
        Env = new Dictionary<string, string>(e.Env),
        Ports = [.. e.Ports],
        Dependencies = [.. e.Dependencies],
        AdditionalInfo = new Dictionary<string, string>(e.AdditionalInfo)
    };

    public static ServiceExplanation FromWire(ServiceExplanationInfo e) => new()
    {
        Name = e.Name,
        Image = e.Image,
        ImageTag = e.ImageTag,
        Count = e.Count,
        Env = new Dictionary<string, string>(e.Env),
        Ports = [.. e.Ports],
        Dependencies = [.. e.Dependencies],
        AdditionalInfo = new Dictionary<string, string>(e.AdditionalInfo)
    };

    public static ServiceDependencyInfo ToWire(ServiceDependency d) => new(d.From, d.To);

    public static ServiceDependency FromWire(ServiceDependencyInfo d) => new() { From = d.From, To = d.To };

    public static DiffEntryInfo ToWire(DiffEntry d) => new()
    {
        ServiceName = d.ServiceName,
        Kind = (DiffKindInfo)d.Kind,
        OldValue = d.OldValue,
        NewValue = d.NewValue,
        Field = d.Field,
        Reason = d.Reason
    };

    public static DiffEntry FromWire(DiffEntryInfo d) => new()
    {
        ServiceName = d.ServiceName,
        Kind = (DiffKind)d.Kind,
        OldValue = d.OldValue,
        NewValue = d.NewValue,
        Field = d.Field,
        Reason = d.Reason
    };

    public static ServiceSnapshotInfo ToWire(ServiceSnapshot s) => new()
    {
        ServiceName = s.ServiceName, Existed = s.Existed, Revision = s.Revision, Data = new Dictionary<string, string>(s.Data)
    };

    public static ServiceSnapshot FromWire(ServiceSnapshotInfo s) => new()
    {
        ServiceName = s.ServiceName, Existed = s.Existed, Revision = s.Revision, Data = new Dictionary<string, string>(s.Data)
    };

    public static AppliedRevisionInfo ToWire(AppliedRevision r) => new()
    {
        ServiceName = r.ServiceName, Success = r.Success, Revision = r.Revision, Error = r.Error
    };

    public static AppliedRevision FromWire(AppliedRevisionInfo r) => new()
    {
        ServiceName = r.ServiceName, Success = r.Success, Revision = r.Revision, Error = r.Error
    };

    public static HealthObservationInfo ToWire(HealthObservation h) => new((HealthStateInfo)h.State, h.Detail, h.Revision);

    public static HealthObservation FromWire(HealthObservationInfo h) => new((HealthState)h.State, h.Detail, h.Revision);

    public static ManagedServiceInfo ToWire(ManagedService m) => new(m.Name, m.TargetId, m.Environment);

    public static ManagedService FromWire(ManagedServiceInfo m) => new(m.Name, m.TargetId, m.Environment);

    public static DeleteOptions DeleteOptionsFromWire(DeleteRequest r) => new() { KeepSecrets = r.KeepSecrets, RemoveVolumes = r.RemoveVolumes };
}
