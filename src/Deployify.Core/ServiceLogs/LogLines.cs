using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Deployify.Core.ServiceLogs;

/// <summary>Helpers targets share to turn their platform's output into <see cref="ServiceLogLine"/>s.</summary>
public static class LogLines
{
    /// <summary>
    /// Reads several streams (one per instance, or stdout and stderr) as one. Without
    /// <paramref name="follow"/>, every stream is read to its end and the lines come out in
    /// timestamp order (lines without a timestamp keep their place in their stream). With it,
    /// lines come out as they arrive. The first stream to fail stops the others, and its
    /// exception is thrown.
    /// </summary>
    public static IAsyncEnumerable<ServiceLogLine> CombineAsync(
        IReadOnlyList<IAsyncEnumerable<ServiceLogLine>> streams, bool follow, CancellationToken ct = default)
        => streams.Count switch
        {
            0 => AsyncEnumerable.Empty<ServiceLogLine>(),
            1 => streams[0],
            _ => follow ? MergeAsync(streams, ct) : ReadAllInOrderAsync(streams, ct)
        };

    private static async IAsyncEnumerable<ServiceLogLine> ReadAllInOrderAsync(
        IReadOnlyList<IAsyncEnumerable<ServiceLogLine>> streams, [EnumeratorCancellation] CancellationToken ct)
    {
        var all = await Task.WhenAll(streams.Select(async s => await s.ToListAsync(ct)));
        foreach (var line in InTimeOrder(all))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Merges lists that are each in order into one, by timestamp. A line without a timestamp
    /// sorts as its predecessor in its own list did, so it stays with the lines around it.
    /// </summary>
    public static IEnumerable<ServiceLogLine> InTimeOrder(IEnumerable<IReadOnlyList<ServiceLogLine>> lists)
    {
        var keyed = new List<(DateTimeOffset Key, int List, int Index, ServiceLogLine Line)>();
        var listIndex = 0;
        foreach (var list in lists)
        {
            var last = DateTimeOffset.MinValue;
            for (var i = 0; i < list.Count; i++)
            {
                last = list[i].Timestamp ?? last;
                keyed.Add((last, listIndex, i, list[i]));
            }

            listIndex++;
        }

        return keyed
            .OrderBy(k => k.Key)
            .ThenBy(k => k.List)
            .ThenBy(k => k.Index)
            .Select(k => k.Line);
    }

    private static async IAsyncEnumerable<ServiceLogLine> MergeAsync(
        IReadOnlyList<IAsyncEnumerable<ServiceLogLine>> streams, [EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var merged = Channel.CreateBounded<ServiceLogLine>(new BoundedChannelOptions(1000) { SingleReader = true });
        var failure = new StrongBox<Exception?>();
        var pumps = streams.Select(async stream =>
        {
            try
            {
                await foreach (var line in stream.WithCancellation(stop.Token))
                {
                    await merged.Writer.WriteAsync(line, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped: the reader went away, or another stream failed.
            }
            catch (Exception ex)
            {
                // The first failure stops every other stream at once (one may never end on its own).
                lock (failure)
                {
                    failure.Value ??= ex;
                }

                await stop.CancelAsync();
            }
        }).ToList();

        var all = Task.WhenAll(pumps);
        _ = all.ContinueWith(_ => merged.Writer.TryComplete(failure.Value),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var line in merged.Reader.ReadAllAsync(ct))
            {
                yield return line;
            }
        }
        finally
        {
            await stop.CancelAsync();
            await all;
        }
    }

    /// <summary>Keeps the last <paramref name="tail"/> lines of each instance (all of them when null).</summary>
    public static IReadOnlyList<ServiceLogLine> TakeLastPerInstance(IReadOnlyList<ServiceLogLine> lines, int? tail)
    {
        if (tail is not { } n)
        {
            return lines;
        }

        var keep = new HashSet<int>();
        foreach (var group in lines.Select((l, i) => (l, i)).GroupBy(x => x.l.Instance ?? "", StringComparer.Ordinal))
        {
            foreach (var (_, i) in group.TakeLast(Math.Max(0, n)))
            {
                keep.Add(i);
            }
        }

        return [.. lines.Where((_, i) => keep.Contains(i))];
    }

    /// <summary>True when the line is at or after <paramref name="since"/>, or either has no time.</summary>
    public static bool IsSince(ServiceLogLine line, DateTimeOffset? since)
        => since is not { } s || line.Timestamp is not { } t || t >= s;

    /// <summary>
    /// Splits a leading RFC 3339 timestamp off a line, as <c>kubectl logs --timestamps</c>,
    /// <c>docker logs --timestamps</c> and similar print it ("2024-05-01T10:00:00.123456789Z text").
    /// Accepts up to nine fractional digits. Returns null, and the whole line, when there is none.
    /// </summary>
    public static (DateTimeOffset? Timestamp, string Text) SplitTimestamp(string line)
    {
        var space = line.IndexOf(' ');
        var token = space < 0 ? line : line[..space];
        return TryParseTimestamp(token, out var timestamp)
            ? (timestamp, space < 0 ? "" : line[(space + 1)..])
            : (null, line);
    }

    /// <summary>Parses an RFC 3339 / ISO 8601 time with up to nine fractional digits (.NET reads seven).</summary>
    public static bool TryParseTimestamp(string text, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (text.Length < 20 || text[4] != '-' || text[10] is not ('T' or 't' or ' '))
        {
            return false;
        }

        // Cut the fraction to seven digits, which is all a DateTimeOffset holds anyway.
        var dot = text.IndexOf('.', 19);
        if (dot == 19)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = text[..(dot + 8)] + text[end..];
            }
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    /// <summary>
    /// Reads <c>--since</c>: a duration back from <paramref name="now"/> ("90s", "10m", "2h",
    /// "1d", or combined like "1h30m") or a point in time ("2024-05-01T10:00:00Z", "2024-05-01").
    /// </summary>
    public static bool TryParseSince(string text, DateTimeOffset now, out DateTimeOffset since)
    {
        since = default;
        text = text.Trim();
        if (TryParseDuration(text, out var duration))
        {
            since = now - duration;
            return true;
        }

        if (TryParseTimestamp(text, out since))
        {
            return true;
        }

        return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out since);
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (text.Length == 0)
        {
            return false;
        }

        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == start || i == text.Length ||
                !long.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var unit = text[i++] switch
            {
                's' => TimeSpan.FromSeconds(1),
                'm' => TimeSpan.FromMinutes(1),
                'h' => TimeSpan.FromHours(1),
                'd' => TimeSpan.FromDays(1),
                _ => TimeSpan.Zero
            };
            if (unit == TimeSpan.Zero || value > 100_000)
            {
                return false;
            }

            duration += unit * value;
        }

        return true;
    }
}
