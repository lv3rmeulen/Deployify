using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Deployify.Plugin.Contracts;
using Microsoft.Extensions.Logging;

namespace Deployify.Plugin.Sdk;

/// <summary>
/// One open <c>logs/open</c> stream: the target's lines, read ahead into a buffer, handed out a
/// batch per <c>logs/next</c>. A full buffer pauses the target until Deployify reads again.
/// </summary>
internal sealed class LogStream : IAsyncDisposable
{
    private const int Buffer = 10_000;

    private readonly Channel<ServiceLogLine> _lines = Channel.CreateBounded<ServiceLogLine>(
        new BoundedChannelOptions(Buffer) { SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public LogStream(IAsyncEnumerable<ServiceLogLine> source, ILogger logger)
    {
        _pump = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await foreach (var line in source.WithCancellation(_stop.Token))
                {
                    await _lines.Writer.WriteAsync(line, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Closed.
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Reading logs failed.");
                failure = ex;
            }

            _lines.Writer.TryComplete(failure);
        });
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Up to <paramref name="max"/> lines. Waits up to <paramref name="wait"/> for the first; then
    /// takes what is there. Done once the target has ended and everything was handed out. A
    /// target that failed throws its exception once the lines before the failure are out.
    /// </summary>
    public async Task<LogsBatch> NextAsync(int max, TimeSpan wait, CancellationToken ct)
    {
        var batch = new List<LogLineInfo>();
        var reader = _lines.Reader;
        if (!reader.TryPeek(out _) && !reader.Completion.IsCompleted)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            try
            {
                await reader.WaitToReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new LogsBatch(batch, Done: false);
            }
            catch (ChannelClosedException)
            {
                // Failed: thrown below, after nothing else is left.
            }
        }

        while (batch.Count < max && reader.TryRead(out var line))
        {
            batch.Add(new LogLineInfo(line.Text, line.Instance, line.Timestamp, line.IsError));
        }

        if (batch.Count == 0 && reader.Completion.IsCompleted)
        {
            // Rethrows the target's exception, if it failed.
            await reader.Completion;
            return new LogsBatch(batch, Done: true);
        }

        return new LogsBatch(batch, Done: reader.Completion.IsCompletedSuccessfully);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        // A target that ignores cancellation mustn't hold up the session's end.
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(5)));
        if (_pump.IsCompleted)
        {
            _stop.Dispose();
        }
    }
}
