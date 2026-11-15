using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Deployify.Core.ResourceSources;
using Microsoft.Extensions.Logging;

namespace Deployify.ResourceServer;

/// <summary>
/// Watches the source once, however many dashboards are connected, and fans the changes out.
/// Keeps the current set so a dashboard that connects (or reconnects) gets it straight away.
/// </summary>
internal sealed class ResourceHub : IAsyncDisposable
{
    private readonly IResourceSource _source;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<Channel<IReadOnlyList<ResourceChange>>> _subscribers = [];
    private readonly TaskCompletionSource _firstSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<string, ResourceSnapshot> _current = new(StringComparer.Ordinal);
    private Task? _loop;

    public ResourceHub(IResourceSource source, ILogger logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <summary>How long a new subscriber waits for the source's first set before it gets an empty one.</summary>
    public TimeSpan FirstSetTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public IReadOnlyCollection<ResourceSnapshot> Current
    {
        get
        {
            lock (_gate)
            {
                return [.. _current.Values];
            }
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>
    /// The current set (parents first) and a stream of the changes after it. Dispose the
    /// subscription to stop receiving.
    /// </summary>
    public async Task<Subscription> SubscribeAsync(CancellationToken ct)
    {
        Start();
        try
        {
            await _firstSet.Task.WaitAsync(FirstSetTimeout, ct);
        }
        catch (TimeoutException)
        {
            // The platform is slow or unreachable: show an empty list now, the resources arrive as changes.
        }

        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceChange>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_gate)
        {
            _subscribers.Add(channel);
            return new Subscription(this, channel, ResourceSetDiff.ParentsFirst(_current.Values));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var set in _source.WatchAsync(ct))
            {
                Publish(set);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watching {Source} stopped.", _source.DisplayName);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
    }

    internal void Publish(IReadOnlyList<ResourceSnapshot> set)
    {
        lock (_gate)
        {
            var changes = Order(ResourceSetDiff.Compare(_current, set));
            _current = ResourceSetDiff.ToMap(set);
            if (changes.Count > 0)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(changes);
                }
            }
        }

        _firstSet.TrySetResult();
    }

    /// <summary>Upserts parents before children, then deletions children before parents.</summary>
    private static IReadOnlyList<ResourceChange> Order(IReadOnlyList<ResourceChange> changes)
        =>
        [
            .. changes.Where(c => c.Upsert is not null).OrderBy(c => c.Upsert!.Parent is null ? 0 : 1),
            .. changes.Where(c => c.Deleted is not null).OrderBy(c => c.Deleted!.Parent is null ? 1 : 0)
        ];

    private void Unsubscribe(Channel<IReadOnlyList<ResourceChange>> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        _stop.Dispose();
    }

    internal sealed class Subscription(
        ResourceHub hub, Channel<IReadOnlyList<ResourceChange>> channel, IReadOnlyList<ResourceSnapshot> initial) : IDisposable
    {
        public IReadOnlyList<ResourceSnapshot> Initial { get; } = initial;

        public ChannelReader<IReadOnlyList<ResourceChange>> Changes => channel.Reader;

        public void Dispose() => hub.Unsubscribe(channel);
    }
}
