using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Deployify.Core.ResourceSources;

namespace Deployify.ResourceServer.Tests;

/// <summary>A source the test drives: it publishes sets, log lines and command results by hand.</summary>
internal sealed class FakeResourceSource : IResourceSource
{
    private readonly Channel<IReadOnlyList<ResourceSnapshot>> _sets = Channel.CreateUnbounded<IReadOnlyList<ResourceSnapshot>>();

    public string DisplayName => "Fake";

    public Dictionary<string, List<ResourceLogLine>> Logs { get; } = [];

    /// <summary>Lines sent after the recorded ones while following.</summary>
    public Channel<ResourceLogLine> LiveLogs { get; } = Channel.CreateUnbounded<ResourceLogLine>();

    public List<(string Resource, string Command)> Commands { get; } = [];

    public ResourceCommandResult CommandResult { get; set; } = ResourceCommandResult.Succeeded("done");

    public int RefreshRequests { get; private set; }

    public void Publish(params ResourceSnapshot[] set) => _sets.Writer.TryWrite(set);

    public async IAsyncEnumerable<IReadOnlyList<ResourceSnapshot>> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var set in _sets.Reader.ReadAllAsync(ct))
        {
            yield return set;
        }
    }

    public async IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(
        string resourceName, bool follow, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var line in Logs.GetValueOrDefault(resourceName) ?? [])
        {
            yield return line;
        }

        if (!follow)
        {
            yield break;
        }

        await foreach (var line in LiveLogs.Reader.ReadAllAsync(ct))
        {
            yield return line;
        }
    }

    public Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct)
    {
        Commands.Add((resourceName, commandName));
        return Task.FromResult(CommandResult);
    }

    public void RequestRefresh() => RefreshRequests++;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
