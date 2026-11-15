using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.ResourceSources;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.ResourceSources;

public sealed class ResourceSetDiffTests
{
    private static ResourceSnapshot R(string name, string? state = "Running", string? parent = null) =>
        new() { Name = name, ResourceType = "T", State = state, Parent = parent, Urls = [new ResourceUrl(null, $"http://{name}")] };

    [Fact]
    public void Compare_FindsAddedChangedAndRemoved_IgnoringUnchanged()
    {
        var before = ResourceSetDiff.ToMap([R("a"), R("b"), R("c")]);

        var changes = ResourceSetDiff.Compare(before, [R("a"), R("b", "Exited"), R("d")]);

        changes.Where(c => c.Upsert is not null).Select(c => c.Upsert!.Name).Order().ShouldBe(["b", "d"]);
        changes.Where(c => c.Deleted is not null).Select(c => c.Deleted!.Name).ShouldBe(["c"]);
    }

    [Fact]
    public void AreEqual_ComparesListsByContent()
    {
        // Every poll builds new lists; equal content must not count as a change.
        ResourceSetDiff.AreEqual(R("a"), R("a")).ShouldBeTrue();
        ResourceSetDiff.AreEqual(R("a"), R("a") with { Urls = [new ResourceUrl(null, "http://other")] }).ShouldBeFalse();
    }

    [Fact]
    public void ParentsFirst_PutsChildrenAfterTheirParents()
        => ResourceSetDiff.ParentsFirst([R("pod", parent: "svc"), R("svc")]).Select(r => r.Name).ShouldBe(["svc", "pod"]);
}

public sealed class PollingResourceSourceTests
{
    private sealed class FlakySource(Queue<Func<IReadOnlyList<ResourceSnapshot>>> polls)
        : PollingResourceSource(new ResourceSourceOptions { PollInterval = TimeSpan.FromMilliseconds(5) }, NullLogger.Instance)
    {
        public override string DisplayName => "flaky";

        protected override Task<IReadOnlyList<ResourceSnapshot>> GetResourcesAsync(CancellationToken ct)
            => Task.FromResult(polls.Count > 0 ? polls.Dequeue()() : []);

        public override IAsyncEnumerable<ResourceLogLine> WatchLogsAsync(string resourceName, bool follow, CancellationToken ct)
            => AsyncEnumerable.Empty<ResourceLogLine>();

        public override Task<ResourceCommandResult> ExecuteCommandAsync(string resourceName, string commandName, CancellationToken ct)
            => Task.FromResult(ResourceCommandResult.Succeeded());
    }

    [Fact]
    public async Task AFailedPoll_IsSkipped_NotReportedAsAnEmptySet()
    {
        var ok = new ResourceSnapshot { Name = "a", ResourceType = "T" };
        await using var source = new FlakySource(new Queue<Func<IReadOnlyList<ResourceSnapshot>>>(
        [
            () => [ok],
            () => throw new InvalidOperationException("unreachable"),
            () => [ok, ok with { Name = "b" }]
        ]));

        var sets = new List<int>();
        await foreach (var set in source.WatchAsync(TestContext.Current.CancellationToken))
        {
            sets.Add(set.Count);
            if (sets.Count == 2)
            {
                break;
            }
        }

        sets.ShouldBe([1, 2]);
    }
}

public sealed class ProcessStreamingTests
{
    [Fact]
    public async Task StreamAsync_YieldsStdoutAndStderrLines_AsTheProgramWritesThem()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses sh.");

        var lines = new List<ProcessOutputLine>();
        await foreach (var line in ProcessRunner.Default.StreamAsync(
                           new ProcessSpec("sh", ["-c", "echo one; echo oops >&2; echo two"]), TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        lines.Where(l => !l.IsStdErr).Select(l => l.Text).ShouldBe(["one", "two"]);
        lines.Single(l => l.IsStdErr).Text.ShouldBe("oops");
    }

    [Fact]
    public async Task StreamAsync_KillsTheProgram_WhenTheConsumerStops()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses sh.");

        // A program that would print forever; leaving the loop must not leave it running.
        var started = DateTime.UtcNow;
        await foreach (var line in ProcessRunner.Default.StreamAsync(
                           new ProcessSpec("sh", ["-c", "while true; do echo tick; sleep 0.05; done"]), TestContext.Current.CancellationToken))
        {
            line.Text.ShouldBe("tick");
            break;
        }

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheDefaultStreamAsync_RunsToCompletion_ThenYieldsTheOutput()
    {
        IProcessRunner fake = new Deployify.Tests.Shared.FakeProcessRunner { Default = new ProcessResult(0, "a\r\nb\n", "err\n") };

        var lines = new List<ProcessOutputLine>();
        await foreach (var line in fake.StreamAsync(new ProcessSpec("x", []), TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        lines.ShouldBe([new("a", false), new("b", false), new("err", true)]);
    }

    [Fact]
    public async Task StreamAsync_ThrowsToolNotFound_ForAMissingProgram()
        => await Should.ThrowAsync<ToolNotFoundException>(async () =>
        {
            await foreach (var _ in ProcessRunner.Default.StreamAsync(
                               new ProcessSpec("deployify-no-such-tool", []), TestContext.Current.CancellationToken))
            {
            }
        });
}
