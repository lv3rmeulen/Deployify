using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.State;

/// <summary>
/// What Deployify has deployed in one environment. Together with the ownership marker on each
/// platform resource this decides what destroy and prune may delete: a resource needs both,
/// so neither a stale inventory nor a resource someone tagged by hand is enough to delete it.
/// </summary>
public sealed class EnvironmentInventory
{
    /// <summary>Set once, when the inventory is created; a push refuses to replace one lineage with another.</summary>
    public string Lineage { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Goes up by one on every write.</summary>
    public long Serial { get; set; }

    public string Environment { get; set; } = string.Empty;

    /// <summary>Display name of the target the resources were deployed to.</summary>
    public string? Target { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ManagedResource> Resources { get; set; } = [];

    public ManagedResource? Find(string name, string kind = ManagedResource.ServiceKind)
        => Resources.FirstOrDefault(r => r.Name == name && r.Kind == kind);

    public IEnumerable<ManagedResource> Services => Resources.Where(r => r.Kind == ManagedResource.ServiceKind);

    /// <summary>Adds or refreshes a service; keeps when it was first deployed.</summary>
    public ManagedResource Upsert(string name, IEnumerable<string> dependsOn, DateTimeOffset now, string? targetId = null)
    {
        var existing = Find(name);
        if (existing is null)
        {
            existing = new ManagedResource { Name = name, CreatedAt = now };
            Resources.Add(existing);
        }

        existing.DependsOn = [.. dependsOn.Distinct().Order(StringComparer.Ordinal)];
        existing.UpdatedAt = now;
        existing.TargetId = targetId ?? existing.TargetId;
        return existing;
    }

    public bool Remove(string name, string kind = ManagedResource.ServiceKind)
        => Resources.RemoveAll(r => r.Name == name && r.Kind == kind) > 0;
}

public sealed class ManagedResource
{
    public const string ServiceKind = "service";

    public string Kind { get; set; } = ServiceKind;

    /// <summary>The Aspire resource name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The platform's id for it (an ARN, a resource id), when the target reports one.</summary>
    public string? TargetId { get; set; }

    /// <summary>Services this one uses, so destroy can remove dependents first.</summary>
    public List<string> DependsOn { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Reads and writes an environment's inventory with compare-and-swap. <see cref="UpdateAsync"/>
/// re-reads and re-applies the change when someone else wrote in between, so concurrent
/// writers merge instead of losing each other's entries.
/// </summary>
public sealed class InventoryStore(IStateBackend backend, string key, string environment, TimeProvider? time = null)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Key { get; } = key;

    /// <summary>The inventory, or an empty one (not yet written) when there is none.</summary>
    public async Task<EnvironmentInventory> LoadAsync(CancellationToken ct = default)
        => (await ReadAsync(ct)).Inventory;

    /// <summary>Applies <paramref name="change"/> and saves, retrying on conflicts. Returns the saved inventory.</summary>
    public async Task<EnvironmentInventory> UpdateAsync(Action<EnvironmentInventory> change, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (inventory, etag) = await ReadAsync(ct);
            change(inventory);
            inventory.Serial++;
            inventory.UpdatedAt = _time.GetUtcNow();
            inventory.Environment = environment;
            try
            {
                await backend.WriteAsync(Key, JsonSerializer.SerializeToUtf8Bytes(inventory, Json), etag, ct);
                return inventory;
            }
            catch (StateConflictException) when (attempt < 4)
            {
                // Someone else updated it; apply our change to their version.
            }
        }
    }

    /// <summary>Replaces the stored inventory wholesale (used by <c>state push</c>).</summary>
    public async Task ReplaceAsync(EnvironmentInventory inventory, string? expectedETag, CancellationToken ct = default)
        => await backend.WriteAsync(Key, JsonSerializer.SerializeToUtf8Bytes(inventory, Json), expectedETag, ct);

    public async Task<(EnvironmentInventory Inventory, string? ETag)> ReadAsync(CancellationToken ct = default)
    {
        var blob = await backend.ReadAsync(Key, ct);
        if (blob is null)
        {
            return (new EnvironmentInventory { Environment = environment }, null);
        }

        var inventory = JsonSerializer.Deserialize<EnvironmentInventory>(blob.Data, Json)
                        ?? throw new StateBackendException($"State '{Key}' is not a valid inventory.");
        return (inventory, blob.ETag);
    }
}
