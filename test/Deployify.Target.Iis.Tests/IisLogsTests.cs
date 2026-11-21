using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Iis.Tests;

/// <summary><c>deployify logs</c> on IIS: the script's Logs action, and following by asking again with a cursor.</summary>
public sealed class IisLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Live(string service) => new { service, environment = "prod", siteName = $"prod-{service}", fingerprint = "f", release = "r", siteExists = true };

    private static FakeProcessRunner Server(params object[] answers)
    {
        var queue = new Queue<object>(answers);
        return Fixtures.Server(Live("api")).When(
            spec => Fixtures.IsAction(spec, "Logs"),
            _ => new ProcessResult(0, "##deployify " + JsonSerializer.Serialize(queue.Count > 1 ? queue.Dequeue() : queue.Peek()), ""));
    }

    [Fact]
    public async Task ReadsTheSitesStdoutFilesAndEventLogEntries_KeepingTheOutputOutOfTheLog()
    {
        var fake = Server(new
        {
            captured = true,
            lines = new object[]
            {
                new { text = "Now listening on: http://localhost:5000", instance = "pid-4242", error = false },
                new { text = "Application '/LM/W3SVC/2/ROOT' failed to start.", instance = "event-log", time = "2026-11-21T10:00:00.0000000Z", error = true }
            },
            cursor = new { file = "stdout_20261121100000_4242.log", offset = 120, record = 77 }
        });

        var lines = await Fixtures.Target(fake).ReadLogsAsync(Fixtures.Config(), new ServiceLogQuery { Service = "api", Tail = 50 }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text, l.IsError)).ShouldBe(
        [
            ("pid-4242", "Now listening on: http://localhost:5000", false),
            ("event-log", "Application '/LM/W3SVC/2/ROOT' failed to start.", true)
        ]);
        lines[1].Timestamp.ShouldBe(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));

        var call = Fixtures.Call(fake, "Logs");
        call.Sensitive.ShouldBeTrue();
        using var request = JsonDocument.Parse(call.Stdin!);
        request.RootElement.GetProperty("siteName").GetString().ShouldBe("prod-api");
        request.RootElement.GetProperty("tail").GetInt32().ShouldBe(50);
        request.RootElement.GetProperty("follow").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Following_AsksAgainWithTheCursorItWasGiven()
    {
        var fake = Server(
            new { captured = true, lines = new object[] { new { text = "first", instance = "pid-1" } }, cursor = new { file = "a.log", offset = 10, record = 5 } },
            new { captured = true, lines = new { text = "second", instance = "pid-1" }, cursor = new { file = "a.log", offset = 20, record = 5 } });
        var target = new IisDeploymentTarget(NullLogger<IisDeploymentTarget>.Instance) { Processes = fake, LogPollInterval = TimeSpan.Zero };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var seen = new List<string>();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var line in target.ReadLogsAsync(Fixtures.Config(), new ServiceLogQuery { Service = "api", Follow = true }, cts.Token))
            {
                seen.Add(line.Text);
                if (seen.Count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });

        seen.ShouldBe(["first", "second", "second"]);
        var requests = fake.Calls.Where(c => Fixtures.IsAction(c, "Logs")).Select(c => JsonDocument.Parse(c.Stdin!).RootElement).ToList();
        requests[0].TryGetProperty("cursor", out _).ShouldBeFalse();
        requests[1].GetProperty("cursor").GetProperty("offset").GetInt32().ShouldBe(10);
        requests[2].GetProperty("cursor").GetProperty("offset").GetInt32().ShouldBe(20);
        requests[1].GetProperty("tail").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task CaptureStdout_ReachesTheSite_AndOnlyChangesTheFingerprintWhenOn()
    {
        var off = Fixtures.Config();
        var on = Fixtures.Config(i => i.CaptureStdout = true);
        var gen = new IisSiteGenerator();
        var siteOff = (await gen.GenerateAsync(Fixtures.Manifest(), off, IisDeploymentTarget.GetConfig(off), ct: Ct)).Single(s => s.Service == "api");
        var siteOn = (await gen.GenerateAsync(Fixtures.Manifest(), on, IisDeploymentTarget.GetConfig(on), ct: Ct)).Single(s => s.Service == "api");

        siteOn.CaptureStdout.ShouldBeTrue();
        IisDeploymentTarget.SitePayload(siteOn, "f", "r")["captureStdout"]!.GetValue<bool>().ShouldBeTrue();
        IisDeploymentTarget.SitePayload(siteOff, "f", "r").AsObject().ContainsKey("captureStdout").ShouldBeFalse();
        IisDeploymentTarget.Fingerprint(siteOn, "p").ShouldNotBe(IisDeploymentTarget.Fingerprint(siteOff, "p"));
    }

    [Fact]
    public void TheScript_OffersTheLogsAction()
    {
        IisRemote.Script.ShouldContain("'Logs'");
        IisRemote.Script.ShouldContain("Enable-StdoutLog");
    }
}
