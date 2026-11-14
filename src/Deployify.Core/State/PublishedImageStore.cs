using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Core.State;

/// <summary>
/// Keeps the digests of published images in the environment's deployment state
/// (<c>{prefix}/{env}/images.json</c>), so a later run (an apply after a publish, a rollback, a
/// drift check in another CI job) references exactly the images that were pushed.
/// </summary>
public static class PublishedImageStore
{
    /// <summary>Digests kept per repository; older builds drop off.</summary>
    public const int KeepPerRepository = 20;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Key(StateSession state) => state.Root + "images.json";

    /// <summary>Adds the stored digests to <paramref name="images"/>. Nothing stored yet is fine.</summary>
    public static async Task LoadIntoAsync(StateSession state, PublishedImages images, CancellationToken ct = default)
    {
        if (await state.Backend.ReadAsync(Key(state), ct) is { } blob)
        {
            images.Load(Read(blob.Data));
        }
    }

    /// <summary>
    /// Merges the digests recorded in this run into the stored ones. Entries other runs stored
    /// in the meantime are kept: the write is a compare-and-swap that retries on conflict.
    /// </summary>
    public static async Task SaveAsync(StateSession state, PublishedImages images, CancellationToken ct = default)
    {
        if (!images.HasChanges)
        {
            return;
        }

        await state.PutAsync(Key(state), current =>
        {
            var merged = current is null ? new Dictionary<string, PublishedImage>(StringComparer.Ordinal) : Read(current);
            foreach (var (key, image) in images.Recorded)
            {
                merged[key] = image;
            }

            return JsonSerializer.SerializeToUtf8Bytes(PublishedImages.Trim(merged, KeepPerRepository), s_json);
        }, ct);
    }

    private static Dictionary<string, PublishedImage> Read(byte[] data)
        => JsonSerializer.Deserialize<Dictionary<string, PublishedImage>>(data, s_json)
           ?? new Dictionary<string, PublishedImage>(StringComparer.Ordinal);
}
