using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary><c>deployify logs</c> on Nomad: which allocations and tasks are read, and how the tail is taken.</summary>
public sealed class NomadLogsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNomadLogs _nomad = new();

    private const string Allocations = """
    [
      { "ID": "aaaaaaaa-1111", "ClientStatus": "running", "CreateTime": 2, "TaskStates": { "api-dapr": {}, "api": {} } },
      { "ID": "bbbbbbbb-2222", "ClientStatus": "complete", "CreateTime": 1, "TaskStates": { "api": {} } },
      { "ID": "cccccccc-3333", "ClientStatus": "pending", "CreateTime": 3, "TaskStates": { "api": {} } }
    ]
    """;

    public NomadLogsTests()
    {
        _nomad.Routes["/v1/job/api"] = """{ "ID": "api", "Version": 1, "Status": "running" }""";
        _nomad.Routes["/v1/job/api/allocations"] = Allocations;
    }

    public void Dispose() => _nomad.Dispose();

    private NomadClient Client() => new(_nomad.Address, null, NullLogger<NomadClient>.Instance);

    [Fact]
    public async Task ReadsTheMainTaskOfEachRunningAllocation_StdoutAndStderr()
    {
        _nomad.Logs[("aaaaaaaa-1111", "stdout")] = "a1\na2\na3\n";
        _nomad.Logs[("aaaaaaaa-1111", "stderr")] = "a-err\n";
        _nomad.Logs[("cccccccc-3333", "stdout")] = "c1\n";
        using var client = Client();

        var lines = await NomadLogs.ReadAsync(client, "api", new ServiceLogQuery { Service = "api", Tail = 2 }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text, l.IsError)).ShouldBe(
        [
            ("aaaaaaaa", "a2", false), ("aaaaaaaa", "a3", false), ("aaaaaaaa", "a-err", true), ("cccccccc", "c1", false)
        ]);
        _nomad.LogRequests.ShouldNotContain(r => r.Contains("bbbbbbbb"));
        _nomad.LogRequests.ShouldAllBe(r => r.Contains("task=api&"));
    }

    [Fact]
    public async Task ALogShorterThanTheWindow_IsReadFromTheStart_SoItsFirstLineIsKept()
    {
        _nomad.Logs[("aaaaaaaa-1111", "stdout")] = "first\nsecond\n";
        using var client = Client();

        var lines = await NomadLogs.ReadAsync(client, "api", new ServiceLogQuery { Service = "api", Tail = 10 }, Ct).ToListAsync(Ct);

        lines.Where(l => l.Instance == "aaaaaaaa" && !l.IsError).Select(l => l.Text).ShouldBe(["first", "second"]);
        _nomad.LogRequests.ShouldContain(r => r.Contains("aaaaaaaa") && r.Contains("type=stdout") && r.Contains("origin=end&offset=5120"));
        _nomad.LogRequests.ShouldContain(r => r.Contains("aaaaaaaa") && r.Contains("type=stdout") && r.Contains("origin=start"));
    }

    [Fact]
    public async Task WhenNothingRuns_TheNewestAllocationIsRead()
    {
        _nomad.Routes["/v1/job/api/allocations"] = """
        [
          { "ID": "old00000", "ClientStatus": "failed", "CreateTime": 1, "TaskStates": { "api": {} } },
          { "ID": "new00000", "ClientStatus": "failed", "CreateTime": 5, "TaskStates": { "api": {} } }
        ]
        """;
        _nomad.Logs[("new00000", "stderr")] = "crashed\n";
        using var client = Client();

        var lines = await NomadLogs.ReadAsync(client, "api", new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct);

        lines.Single().ShouldBe(new ServiceLogLine { Text = "crashed", Instance = "new00000", IsError = true });
    }

    [Fact]
    public async Task Following_StartsAtTheEnd_AfterTheTail()
    {
        _nomad.Routes["/v1/job/api/allocations"] = """[ { "ID": "aaaaaaaa-1111", "ClientStatus": "running", "TaskStates": { "api": {} } } ]""";
        _nomad.Logs[("aaaaaaaa-1111", "stdout")] = "old\n";
        _nomad.FollowLogs[("aaaaaaaa-1111", "stdout")] = "new\n";
        using var client = Client();

        var lines = await NomadLogs.ReadAsync(client, "api", new ServiceLogQuery { Service = "api", Tail = 5, Follow = true }, Ct).ToListAsync(Ct);

        lines.Select(l => l.Text).ShouldBe(["old", "new"]);
        _nomad.LogRequests.ShouldContain(r => r.Contains("follow=true") && r.Contains("origin=end&offset=0"));
    }

    [Fact]
    public async Task Since_IsRefused_BecauseNomadKeepsNoTimes()
    {
        using var client = Client();
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () => await NomadLogs.ReadAsync(
            client, "api", new ServiceLogQuery { Service = "api", Since = DateTimeOffset.UtcNow }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("--tail");
    }

    [Fact]
    public async Task AnUnknownJob_SaysSo()
    {
        using var client = Client();
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () => await NomadLogs.ReadAsync(
            client, "nope", new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no job 'nope'");
    }

    /// <summary>Nomad's job, allocation and client fs/logs endpoints.</summary>
    private sealed class FakeNomadLogs : IDisposable
    {
        private readonly HttpListener _listener = new();

        public ConcurrentDictionary<string, string> Routes { get; } = new();

        /// <summary>A task's whole log, by allocation and stream.</summary>
        public ConcurrentDictionary<(string Alloc, string Type), string> Logs { get; } = new();

        /// <summary>What a following request gets after the end of the log.</summary>
        public ConcurrentDictionary<(string Alloc, string Type), string> FollowLogs { get; } = new();

        public ConcurrentQueue<string> LogRequests { get; } = new();

        public string Address { get; }

        public FakeNomadLogs()
        {
            using (var l = new TcpListener(IPAddress.Loopback, 0))
            {
                l.Start();
                Address = $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
            }

            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var url = ctx.Request.Url!;
                string? body;
                if (url.AbsolutePath.StartsWith("/v1/client/fs/logs/", StringComparison.Ordinal))
                {
                    LogRequests.Enqueue(url.PathAndQuery);
                    body = LogBody(url.AbsolutePath["/v1/client/fs/logs/".Length..], ctx.Request.QueryString);
                }
                else
                {
                    body = Routes.TryGetValue(url.AbsolutePath, out var found) ? found : null;
                }

                ctx.Response.StatusCode = body is null ? 404 : 200;
                var bytes = Encoding.UTF8.GetBytes(body ?? "not found");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private string? LogBody(string alloc, System.Collections.Specialized.NameValueCollection q)
        {
            var key = (alloc, q["type"]!);
            if (q["follow"] == "true")
            {
                return FollowLogs.TryGetValue(key, out var more) ? more : Logs.ContainsKey(key) ? "" : null;
            }

            if (!Logs.TryGetValue(key, out var all))
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(all);
            if (q["origin"] == "end")
            {
                var offset = int.Parse(q["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
                return Encoding.UTF8.GetString(bytes, Math.Max(0, bytes.Length - offset), Math.Min(bytes.Length, offset));
            }

            return all;
        }

        public void Dispose() => _listener.Close();
    }
}
