using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class LogLinesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-21T10:00:00.123456789Z hello world", "2026-11-21T10:00:00.1234567+00:00", "hello world")]
    [InlineData("2026-11-21T10:00:00Z x", "2026-11-21T10:00:00+00:00", "x")]
    [InlineData("2026-11-21T10:00:00.123000+00:00 api/api/abc msg", "2026-11-21T10:00:00.123+00:00", "api/api/abc msg")]
    [InlineData("2026-11-21T10:00:00Z ", "2026-11-21T10:00:00+00:00", "")]
    public void SplitTimestamp_ReadsUpToNineFractionalDigits(string line, string expected, string text)
    {
        var (timestamp, rest) = LogLines.SplitTimestamp(line);
        timestamp.ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        rest.ShouldBe(text);
    }

    [Theory]
    [InlineData("plain text line")]
    [InlineData("2026-11-21 not a timestamp")]
    [InlineData("")]
    [InlineData("[pod/a/b] 2026-11-21T10:00:00Z x")]
    public void SplitTimestamp_LeavesLinesWithoutOneAlone(string line)
    {
        var (timestamp, rest) = LogLines.SplitTimestamp(line);
        timestamp.ShouldBeNull();
        rest.ShouldBe(line);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("1h30m", 5400)]
    public void TryParseSince_TakesDurationsBackFromNow(string text, int seconds)
    {
        LogLines.TryParseSince(text, Now, out var since).ShouldBeTrue();
        since.ShouldBe(Now.AddSeconds(-seconds));
    }

    [Fact]
    public void TryParseSince_TakesPointsInTime()
    {
        LogLines.TryParseSince("2026-11-01T08:30:00Z", Now, out var t).ShouldBeTrue();
        t.ShouldBe(new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero));

        LogLines.TryParseSince("2026-11-01", Now, out var d).ShouldBeTrue();
        d.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("m")]
    [InlineData("10x")]
    [InlineData("yesterday")]
    [InlineData("-5m")]
    public void TryParseSince_RejectsTheRest(string text)
        => LogLines.TryParseSince(text, Now, out _).ShouldBeFalse();

    [Fact]
    public void InTimeOrder_MergesByTime_KeepingUntimedLinesWithTheirNeighbours()
    {
        var a = new List<ServiceLogLine>
        {
            Line("a1", 1), Line("a-untimed"), Line("a3", 3)
        };
        var b = new List<ServiceLogLine> { Line("b2", 2), Line("b4", 4) };

        LogLines.InTimeOrder([a, b]).Select(l => l.Text).ShouldBe(["a1", "a-untimed", "b2", "a3", "b4"]);
    }

    [Fact]
    public void TakeLastPerInstance_KeepsTheTailOfEachInstance_InOrder()
    {
        var lines = new List<ServiceLogLine>
        {
            Line("x1", instance: "x"), Line("y1", instance: "y"), Line("x2", instance: "x"),
            Line("x3", instance: "x"), Line("y2", instance: "y")
        };

        LogLines.TakeLastPerInstance(lines, 2).Select(l => l.Text).ShouldBe(["y1", "x2", "x3", "y2"]);
        LogLines.TakeLastPerInstance(lines, null).Count.ShouldBe(5);
        LogLines.TakeLastPerInstance(lines, 0).ShouldBeEmpty();
    }

    [Fact]
    public async Task CombineAsync_WithoutFollow_ReadsEveryStreamAndSortsByTime()
    {
        var combined = await LogLines.CombineAsync([Stream(Line("a3", 3), Line("a5", 5)), Stream(Line("b1", 1), Line("b4", 4))], follow: false, Ct)
            .ToListAsync(Ct);

        combined.Select(l => l.Text).ShouldBe(["b1", "a3", "b4", "a5"]);
    }

    [Fact]
    public async Task CombineAsync_Following_PassesLinesOnAsTheyArrive()
    {
        var gate = new TaskCompletionSource();
        var combined = LogLines.CombineAsync([Gated(gate.Task, "late"), Stream(Line("early"))], follow: true, Ct);
        var seen = new List<string>();
        await foreach (var line in combined)
        {
            seen.Add(line.Text);
            if (line.Text == "early")
            {
                gate.SetResult();
            }
        }

        seen.ShouldBe(["early", "late"]);
    }

    [Fact]
    public async Task CombineAsync_Following_StopsAtTheFirstFailure_AndThrowsIt()
    {
        var combined = LogLines.CombineAsync([Endless(Ct), Failing("no such pod")], follow: true, Ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await combined.ToListAsync(Ct));
        ex.Message.ShouldBe("no such pod");
    }

    [Fact]
    public async Task CombineAsync_Following_EndsWhenTheReaderIsCancelled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var count = 0;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in LogLines.CombineAsync([Endless(Ct), Endless(Ct)], follow: true, cts.Token))
            {
                if (++count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });
        count.ShouldBeGreaterThanOrEqualTo(3);
    }

    private static ServiceLogLine Line(string text, int? minute = null, string? instance = null)
        => new() { Text = text, Instance = instance, Timestamp = minute is { } m ? Now.AddMinutes(m) : null };

    private static async IAsyncEnumerable<ServiceLogLine> Stream(params ServiceLogLine[] lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }

    private static async IAsyncEnumerable<ServiceLogLine> Gated(Task gate, string text)
    {
        await gate;
        yield return Line(text);
    }

    private static async IAsyncEnumerable<ServiceLogLine> Endless([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            await Task.Delay(5, ct);
            yield return Line("tick");
        }
    }

    private static async IAsyncEnumerable<ServiceLogLine> Failing(string message)
    {
        await Task.Delay(20);
        yield return Line("before");
        throw new InvalidOperationException(message);
    }
}
