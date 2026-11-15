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
            images.Load(Read(blob.Data, Describe(state)));
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
            var merged = current is null ? new Dictionary<string, PublishedImage>(StringComparer.Ordinal) : Read(current, Describe(state));
            foreach (var (key, image) in images.Recorded)
            {
                merged[key] = image;
            }

            return StateFormat.WriteEntries(StateFormat.Images, "images",
                PublishedImages.Trim(merged, KeepPerRepository), s_json);
        }, ct);
    }

    /// <summary>
    /// Reads images.json of any supported format version. Throws <see cref="StateFormatException"/>
    /// for one written by a newer Deployify.
    /// </summary>
    public static Dictionary<string, PublishedImage> Read(byte[] data, string what)
    {
        var entries = StateFormat.ReadEntries(data, "images", StateFormat.Images, what);
        try
        {
            return new Dictionary<string, PublishedImage>(
                entries.Deserialize<Dictionary<string, PublishedImage>>(s_json) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new StateFormatException($"{what} does not hold published images: {ex.Message}", ex);
        }
    }

    private static string Describe(StateSession state) => $"State '{Key(state)}' in {state.Backend.Description}";
}
