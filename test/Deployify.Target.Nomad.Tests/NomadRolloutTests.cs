using System;
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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class NomadRolloutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NomadJobInfo Running(long version) => new(version, "running", Stop: false);

    private static NomadDeploymentInfo Deployment(long version, string status, string? why = null,
        int desired = 2, int healthy = 0, int unhealthy = 0)
        => new(version, status, why, desired, healthy, unhealthy, CreateIndex: version * 10);

    // ── Health evaluation ────────────────────────────────────────────────────

    [Fact]
    public void Successful_IsHealthy()
    {
        var obs = NomadRollout.Evaluate(Running(5), [Deployment(5, "successful", healthy: 2)], 5, autoRevert: true);

        obs.State.ShouldBe(HealthState.Healthy);
        obs.Detail.ShouldBe("2/2 allocation(s) healthy");
    }

    [Fact]
    public void Running_IsProgressing_WithCounts()
    {
        var obs = NomadRollout.Evaluate(Running(5),
            [Deployment(5, "running", healthy: 1, unhealthy: 1), Deployment(4, "successful")], 5, autoRevert: true);

        obs.State.ShouldBe(HealthState.Progressing);
        obs.Detail.ShouldBe("1/2 allocation(s) healthy, 1 unhealthy");
    }

    [Fact]
    public void Failed_WithoutAutoRevert_IsFailed()
    {
        var obs = NomadRollout.Evaluate(Running(5),
            [Deployment(5, "failed", "Failed due to unhealthy allocations")], 5, autoRevert: false);

        obs.State.ShouldBe(HealthState.Failed);
        obs.Detail.ShouldBe("Failed due to unhealthy allocations");
    }

    [Fact]
    public void Failed_AndRevertedByNomad_ReportsTheNewVersion()
    {
        var obs = NomadRollout.Evaluate(Running(6),
            [
                Deployment(6, "running"),
                Deployment(5, "failed", "Failed due to unhealthy allocations - rolling back to job version 4")
            ], 5, autoRevert: true);

        obs.State.ShouldBe(HealthState.RevertedByPlatform);
        obs.Revision.ShouldBe("6");
    }

    [Fact]
    public void Failed_RevertNotRegisteredYet_IsStillProgressing()
    {
        var obs = NomadRollout.Evaluate(Running(5),
            [Deployment(5, "failed", "Failed due to unhealthy allocations - rolling back to job version 4")], 5,
            autoRevert: true);

        obs.State.ShouldBe(HealthState.Progressing);
    }

    [Fact]
    public void NoDeploymentForTheVersion_WaitsForIt()
        => NomadRollout.Evaluate(Running(5), [Deployment(4, "successful")], 5, autoRevert: true)
            .State.ShouldBe(HealthState.Progressing);

    [Fact]
    public void ReplacedByANewerVersion_IsFailed()
        => NomadRollout.Evaluate(Running(7), [Deployment(7, "running")], 5, autoRevert: true)
            .State.ShouldBe(HealthState.Failed);

    [Fact]
    public void JobWithoutDeployments_RunningAtThatVersion_IsHealthy()
        => NomadRollout.Evaluate(Running(5), [], 5, autoRevert: true).State.ShouldBe(HealthState.Healthy);

    [Fact]
    public void StoppedOrMissingJob_IsFailed()
    {
        NomadRollout.Evaluate(null, [], 5, autoRevert: true).State.ShouldBe(HealthState.Failed);
        NomadRollout.Evaluate(new NomadJobInfo(5, "dead", Stop: true), [], 5, autoRevert: true)
            .State.ShouldBe(HealthState.Failed);
    }

    // ── Against a fake Nomad API ─────────────────────────────────────────────

    [Fact]
    public async Task CaptureThenRestore_RevertsJob_AndPutsOldSecretsBackFirst()
    {
        using var nomad = new FakeNomadApi { JobVersion = 4, Variable = new() { ["DB"] = "old" } };
        using var client = Client(nomad);
        var rollout = new NomadRollout(NullLogger.Instance);

        var snapshot = await rollout.CaptureAsync(client, "default", "api", Ct);
        nomad.JobVersion = 5;                           // the rollout deployed version 5 …
        nomad.Variable = new() { ["DB"] = "new" };      // … with new secrets

        var waitFor = await rollout.RestoreAsync(client, "default", snapshot, Ct);

        snapshot.Revision.ShouldBe("4");
        waitFor.ShouldBe("6");                          // the revert registered version 6
        nomad.Variable!["DB"].ShouldBe("old");
        var revert = nomad.Requests.Single(r => r.Path == "/v1/job/api/revert");
        Prop(JsonDocument.Parse(revert.Body).RootElement, "JobVersion").GetInt64().ShouldBe(4);
        nomad.Requests.FindIndex(r => r.Method == "PUT" && r.Path.StartsWith("/v1/var/"))
            .ShouldBeLessThan(nomad.Requests.FindIndex(r => r.Path == "/v1/job/api/revert"));
    }

    [Fact]
    public async Task Restore_UnchangedJob_DoesNotRevert_OrRewriteSecrets()
    {
        using var nomad = new FakeNomadApi { JobVersion = 4, Variable = new() { ["DB"] = "same" } };
        using var client = Client(nomad);
        var rollout = new NomadRollout(NullLogger.Instance);

        var snapshot = await rollout.CaptureAsync(client, "default", "api", Ct);
        var waitFor = await rollout.RestoreAsync(client, "default", snapshot, Ct);

        waitFor.ShouldBe("4");
        nomad.Requests.ShouldNotContain(r => r.Path == "/v1/job/api/revert");
        nomad.Requests.ShouldNotContain(r => r.Method == "PUT");
    }

    [Fact]
    public async Task Restore_NewJob_StopsIt_AndRemovesItsSecrets()
    {
        using var nomad = new FakeNomadApi { JobExists = false };
        using var client = Client(nomad);
        var rollout = new NomadRollout(NullLogger.Instance);

        var snapshot = await rollout.CaptureAsync(client, "default", "api", Ct);
        nomad.JobExists = true;
        nomad.Variable = new() { ["DB"] = "new" };

        var waitFor = await rollout.RestoreAsync(client, "default", snapshot, Ct);

        snapshot.Existed.ShouldBeFalse();
        waitFor.ShouldBeNull();
        nomad.Requests.ShouldContain(r => r.Method == "DELETE" && r.Path == "/v1/job/api");
        nomad.Variable.ShouldBeNull();
    }

    [Fact]
    public async Task GeneratedJob_HasAutoRevert_ByDefault()
    {
        using var dir = new TempDir();
        var target = new NomadDeploymentTarget(new JobGenerator(NullLogger<JobGenerator>.Instance),
            Substitute.For<IPublisher>(), NullLogger<NomadDeploymentTarget>.Instance, NullLoggerFactory.Instance);

        var paths = await target.GenerateAsync(Fixtures.SingleService("api"), Fixtures.NomadConfig(), dir.Path, Ct);

        (await File.ReadAllTextAsync(paths.Single(), Ct)).ShouldContain("auto_revert = true");
        target.HasNativeRollback(Fixtures.NomadConfig()).ShouldBeTrue();
    }

    /// <summary>Nomad matches JSON field names case-insensitively, and the client sends camelCase.</summary>
    private static JsonElement Prop(JsonElement e, string name)
        => e.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static NomadClient Client(FakeNomadApi nomad)
        => new(nomad.Address, null, NullLogger<NomadClient>.Instance);

    /// <summary>
    /// A stateful slice of the Nomad API for one job named "api": read, revert and stop the job,
    /// and read, write and delete its variable.
    /// </summary>
    private sealed class FakeNomadApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();

        public bool JobExists { get; set; } = true;
        public long JobVersion { get; set; } = 1;
        public Dictionary<string, string>? Variable { get; set; }
        public List<(string Method, string Path, string Body)> Requests { get; } = [];

        public FakeNomadApi()
        {
            var port = FreePort();
            Address = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Address { get; }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
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

                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var method = ctx.Request.HttpMethod;
                var path = ctx.Request.Url!.AbsolutePath;
                int status;
                string response;
                lock (_gate)
                {
                    Requests.Add((method, path, body));
                    (status, response) = Handle(method, path, body);
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private (int, string) Handle(string method, string path, string body)
        {
            switch (method, path)
            {
                case ("GET", "/v1/job/api"):
                    return JobExists
                        ? (200, $"{{\"ID\":\"api\",\"Version\":{JobVersion},\"Status\":\"running\",\"Stop\":false}}")
                        : (404, "job not found");
                case ("POST", "/v1/job/api/revert"):
                    JobVersion++;
                    return (200, "{\"EvalID\":\"e1\"}");
                case ("DELETE", "/v1/job/api"):
                    return (200, "{\"EvalID\":\"e2\"}");
                case ("GET", "/v1/var/nomad/jobs/api"):
                    return Variable is null
                        ? (404, "variable not found")
                        : (200, JsonSerializer.Serialize(new { Path = "nomad/jobs/api", Items = Variable }));
                case ("PUT", "/v1/var/nomad/jobs/api"):
                    Variable = Prop(JsonDocument.Parse(body).RootElement, "Items").EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                    return (200, body);
                case ("DELETE", "/v1/var/nomad/jobs/api"):
                    Variable = null;
                    return (200, "");
                default:
                    return (404, "not found");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }

        private static int FreePort()
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
    }
}
