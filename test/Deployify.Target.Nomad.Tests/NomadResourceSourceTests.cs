using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.ResourceSources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>What <c>deployify dashboard</c> shows for a Nomad environment, and what its commands call.</summary>
public sealed class NomadResourceSourceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNomadApi _nomad = new();

    public NomadResourceSourceTests()
    {
        _nomad.Routes["GET /v1/jobs"] = """
        [
          { "ID": "api", "Status": "running", "Stop": false, "JobModifyIndex": 42, "SubmitTime": 1761991200000000000,
            "Meta": { "deployify-managed": "true" } },
          { "ID": "worker", "Status": "dead", "Stop": true, "JobModifyIndex": 7, "Meta": { "deployify-managed": "true" } },
          { "ID": "someone-elses", "Status": "running", "JobModifyIndex": 1, "Meta": null }
        ]
        """;
        _nomad.Routes["GET /v1/job/api"] = """
        {
          "ID": "api", "Version": 5, "Datacenters": ["dc1"],
          "TaskGroups": [ { "Name": "api", "Count": 2, "Tasks": [
            { "Name": "api-dapr", "Driver": "docker", "Config": { "image": "daprio/daprd" } },
            { "Name": "api", "Driver": "docker", "Config": { "image": "registry/api:1.2" },
              "Env": { "ASPNETCORE_URLS": "http://+:8080", "LOG_LEVEL": "debug" } }
          ] } ]
        }
        """;
        _nomad.Routes["GET /v1/job/worker"] = """
        { "ID": "worker", "Version": 2, "Stop": true, "TaskGroups": [ { "Count": 1, "Tasks": [ { "Name": "worker", "Driver": "raw_exec", "Config": { "command": "worker" } } ] } ] }
        """;
        _nomad.Routes["GET /v1/allocations"] = """
        [
          { "ID": "aaaaaaaa-1111-2222-3333-444444444444", "Name": "api.api[0]", "JobID": "api", "JobVersion": 5,
            "ClientStatus": "running", "DesiredStatus": "run", "NodeName": "node-1", "CreateTime": 1761991260000000000,
            "DeploymentStatus": { "Healthy": true },
            "TaskStates": { "api": { "State": "running", "Failed": false, "Restarts": 0, "StartedAt": "2026-11-01T10:01:00Z", "FinishedAt": "0001-01-01T00:00:00Z" } },
            "AllocatedResources": { "Shared": { "Ports": [ { "Label": "http", "Value": 8080, "HostIP": "10.0.0.5" } ] } } },
          { "ID": "bbbbbbbb-1111-2222-3333-444444444444", "Name": "api.api[1]", "JobID": "api", "JobVersion": 5,
            "ClientStatus": "running", "DesiredStatus": "run", "NodeName": "node-2",
            "DeploymentStatus": { "Healthy": false },
            "TaskStates": { "api": { "State": "running", "Restarts": 3 } },
            "AllocatedResources": { "Shared": { "Ports": [ { "Label": "http", "Value": 8080, "HostIP": "10.0.0.6" } ] } } },
          { "ID": "cccccccc-1111-2222-3333-444444444444", "Name": "api.api[0]", "JobID": "api", "JobVersion": 4,
            "ClientStatus": "complete", "DesiredStatus": "stop" }
        ]
        """;
    }

    public void Dispose() => _nomad.Dispose();

    private IResourceSource Source()
    {
        var target = new NomadDeploymentTarget(new JobGenerator(NullLogger<JobGenerator>.Instance), Substitute.For<IPublisher>(),
            NullLogger<NomadDeploymentTarget>.Instance, NullLoggerFactory.Instance);
        var config = new EnvironmentConfig
        {
            TargetConfig = new Dictionary<string, object?>
            {
                ["nomad"] = JsonSerializer.SerializeToElement(new { address = _nomad.Address, @namespace = "shop" })
            }
        };
        return target.CreateResourceSource(config, new ResourceSourceOptions { PollInterval = TimeSpan.FromMilliseconds(20) },
            NullLoggerFactory.Instance);
    }

    private static async Task<IReadOnlyList<ResourceSnapshot>> NextSetAsync(IAsyncEnumerator<IReadOnlyList<ResourceSnapshot>> sets)
    {
        (await sets.MoveNextAsync()).ShouldBeTrue();
        return sets.Current;
    }

    private static async Task<IReadOnlyList<ResourceSnapshot>> FirstSetAsync(IResourceSource source)
    {
        await using var sets = source.WatchAsync(Ct).GetAsyncEnumerator(Ct);
        return await NextSetAsync(sets);
    }

    [Fact]
    public async Task Resources_AreDeployifysJobs_WithTheirActiveAllocationsNested()
    {
        await using var source = Source();

        var set = await FirstSetAsync(source);

        set.Select(r => (r.Name, r.ResourceType, r.Parent)).ShouldBe(
        [
            ("api", "NomadJob", null),
            ("api-aaaaaaaa", "NomadAllocation", "api"),
            ("api-bbbbbbbb", "NomadAllocation", "api"),
            ("worker", "NomadJob", null)
        ]);
        _nomad.Requests.ShouldAllBe(r => r.Contains("namespace=shop"));
    }

    [Fact]
    public async Task AJob_WithAnUnhealthyAllocation_IsUnhealthy_AndShowsItsTaskAndAddresses()
    {
        await using var source = Source();

        var api = (await FirstSetAsync(source)).Single(r => r.Name == "api");

        api.State.ShouldBe(ResourceStates.RuntimeUnhealthy);
        api.HealthReports.Single().ShouldBe(new ResourceHealthReport("allocations", ResourceHealthStatus.Degraded,
            "1/2 allocations running and healthy"));
        api.CreatedAt.ShouldBe(new DateTimeOffset(2025, 11, 1, 10, 0, 0, TimeSpan.Zero));
        api.Urls.Select(u => u.Url).ShouldBe(["http://10.0.0.5:8080", "http://10.0.0.6:8080"]);
        // The job's own task, not the Dapr sidecar.
        api.Properties.Single(p => p.Name == ResourcePropertyNames.ContainerImage).Value.ShouldBe("registry/api:1.2");
        api.Environment.Select(e => (e.Name, e.Value)).ShouldBe([("ASPNETCORE_URLS", "http://+:8080"), ("LOG_LEVEL", "debug")]);
        api.Commands.Select(c => (c.Name, c.Enabled)).ShouldBe([("start", false), ("stop", true), ("restart", true)]);
    }

    [Fact]
    public async Task AStoppedJob_IsExited_AndCanBeStarted()
    {
        await using var source = Source();

        var worker = (await FirstSetAsync(source)).Single(r => r.Name == "worker");

        worker.State.ShouldBe(ResourceStates.Exited);
        worker.Properties.Single(p => p.Name == "nomad.command").Value.ShouldBe("worker");
        worker.Commands.Select(c => (c.Name, c.Enabled)).ShouldBe([("start", true), ("stop", false), ("restart", false)]);
    }

    [Fact]
    public async Task Allocations_ShowTheirOwnStateAndNode()
    {
        await using var source = Source();
        var set = await FirstSetAsync(source);

        var healthy = set.Single(r => r.Name == "api-aaaaaaaa");
        healthy.State.ShouldBe(ResourceStates.Running);
        healthy.StartedAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 10, 1, 0, TimeSpan.Zero));
        healthy.Properties.Single(p => p.Name == "nomad.node").Value.ShouldBe("node-1");
        healthy.Urls.Single().Url.ShouldBe("http://10.0.0.5:8080");

        var unhealthy = set.Single(r => r.Name == "api-bbbbbbbb");
        unhealthy.State.ShouldBe(ResourceStates.RuntimeUnhealthy);
        unhealthy.Properties.Single(p => p.Name == "nomad.restarts").Value.ShouldBe("3");
    }

    [Fact]
    public async Task AJobSpec_IsOnlyFetchedAgain_WhenItsModifyIndexChanges()
    {
        await using var source = Source();
        await using var sets = source.WatchAsync(Ct).GetAsyncEnumerator(Ct);
        await NextSetAsync(sets);
        await NextSetAsync(sets);

        _nomad.Requests.Count(r => r.StartsWith("GET /v1/job/api?", StringComparison.Ordinal)).ShouldBe(1);
        _nomad.Requests.Count(r => r.StartsWith("GET /v1/allocations?", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task JobCommands_StopStartAndRestartAllocations()
    {
        _nomad.Routes["DELETE /v1/job/api"] = "{}";
        _nomad.Routes["POST /v1/jobs"] = "{}";
        _nomad.Routes["POST /v1/client/allocation/aaaaaaaa-1111-2222-3333-444444444444/restart"] = "{}";
        _nomad.Routes["POST /v1/client/allocation/bbbbbbbb-1111-2222-3333-444444444444/restart"] = "{}";
        await using var source = Source();
        await FirstSetAsync(source);

        (await source.ExecuteCommandAsync("api", "stop", Ct)).Success.ShouldBeTrue();
        (await source.ExecuteCommandAsync("worker", "start", Ct)).Success.ShouldBeTrue();
        (await source.ExecuteCommandAsync("api", "restart", Ct)).Success.ShouldBeTrue();

        _nomad.Requests.ShouldContain(r => r.StartsWith("DELETE /v1/job/api?", StringComparison.Ordinal) && r.Contains("purge=false"));
        // Start registers the job's own spec again, with Stop switched off.
        using var registered = JsonDocument.Parse(_nomad.Bodies["POST /v1/jobs"]);
        registered.RootElement.GetProperty("Job").GetProperty("ID").GetString().ShouldBe("worker");
        registered.RootElement.GetProperty("Job").GetProperty("Stop").GetBoolean().ShouldBeFalse();
        _nomad.Requests.Count(r => r.Contains("/restart?")).ShouldBe(2);
    }

    [Fact]
    public async Task AllocationCommands_RestartInPlace_OrReschedule()
    {
        _nomad.Routes["POST /v1/client/allocation/aaaaaaaa-1111-2222-3333-444444444444/restart"] = "{}";
        _nomad.Routes["POST /v1/allocation/bbbbbbbb-1111-2222-3333-444444444444/stop"] = "{}";
        await using var source = Source();
        await FirstSetAsync(source);

        (await source.ExecuteCommandAsync("api-aaaaaaaa", "restart", Ct)).Success.ShouldBeTrue();
        (await source.ExecuteCommandAsync("api-bbbbbbbb", "reschedule", Ct)).Success.ShouldBeTrue();

        _nomad.Bodies["POST /v1/client/allocation/aaaaaaaa-1111-2222-3333-444444444444/restart"].ShouldContain("\"AllTasks\":true");
        _nomad.Requests.ShouldContain(r => r.StartsWith("POST /v1/allocation/bbbbbbbb-1111-2222-3333-444444444444/stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedCommand_IsReportedWithNomadsError()
    {
        await using var source = Source();
        await FirstSetAsync(source);

        var result = await source.ExecuteCommandAsync("api", "stop", Ct); // no route: 404

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull().ShouldContain("404");
    }

    [Fact]
    public async Task Logs_OfAnAllocation_AreItsMainTasksStdoutAndStderr()
    {
        _nomad.Routes["GET /v1/client/fs/logs/aaaaaaaa-1111-2222-3333-444444444444?type=stdout"] = "partial line\nlistening on 8080\n";
        _nomad.Routes["GET /v1/client/fs/logs/aaaaaaaa-1111-2222-3333-444444444444?type=stderr"] = "partial\nwarn: slow\n";
        await using var source = Source();
        await FirstSetAsync(source);

        var lines = new List<ResourceLogLine>();
        await foreach (var line in source.WatchLogsAsync("api-aaaaaaaa", follow: false, Ct))
        {
            lines.Add(line);
        }

        // The first line of each stream starts mid-file, so it is dropped.
        lines.OrderBy(l => l.IsStdErr).ShouldBe([new ResourceLogLine("listening on 8080"), new ResourceLogLine("warn: slow", true)]);
        var request = _nomad.Requests.First(r => r.Contains("type=stdout"));
        request.ShouldContain("task=api&");
        request.ShouldContain("follow=false");
        request.ShouldContain("origin=end");
        request.ShouldContain("plain=true");
    }

    [Fact]
    public async Task Logs_OfAJob_ArePrefixedWithTheirAllocation()
    {
        _nomad.Routes["GET /v1/client/fs/logs/aaaaaaaa-1111-2222-3333-444444444444?type=stdout"] = "x\none\n";
        _nomad.Routes["GET /v1/client/fs/logs/bbbbbbbb-1111-2222-3333-444444444444?type=stdout"] = "x\ntwo\n";
        await using var source = Source();
        await FirstSetAsync(source);

        var lines = new List<string>();
        await foreach (var line in source.WatchLogsAsync("api", follow: false, Ct))
        {
            lines.Add(line.Text);
        }

        // Each allocation's stderr has no route (404): that stream is dropped, the others still arrive.
        lines.Order().ShouldBe(["[aaaaaaaa] one", "[bbbbbbbb] two"]);
    }

    /// <summary>
    /// Answers "METHOD /path" routes (or "METHOD /path?type=…" for log streams) and records every
    /// request with its query, and the body of the last request per route.
    /// </summary>
    private sealed class FakeNomadApi : IDisposable
    {
        private readonly HttpListener _listener = new();

        public ConcurrentDictionary<string, string> Routes { get; } = new();

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentDictionary<string, string> Bodies { get; } = new();

        public string Address { get; }

        public FakeNomadApi()
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

                var request = ctx.Request;
                var route = $"{request.HttpMethod} {request.Url!.AbsolutePath}";
                Requests.Enqueue($"{route}{request.Url.Query}");
                using (var reader = new StreamReader(request.InputStream))
                {
                    Bodies[route] = await reader.ReadToEndAsync();
                }

                var type = request.QueryString["type"];
                var body = (type is not null && Routes.TryGetValue($"{route}?type={type}", out var log)) ? log
                    : Routes.TryGetValue(route, out var found) ? found
                    : null;
                ctx.Response.StatusCode = body is null ? 404 : 200;
                var bytes = Encoding.UTF8.GetBytes(body ?? "not found");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
