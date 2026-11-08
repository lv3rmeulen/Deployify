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
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>What destroy and prune do on Nomad: purge the job, and its variables unless secrets are kept.</summary>
public sealed class NomadLifecycleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNomad _nomad = new();

    private NomadClient Client() => new(_nomad.Address, null, NullLogger<NomadClient>.Instance);

    [Fact]
    public async Task ListManaged_IncludesStoppedJobs_ButOnlyDeployifys()
    {
        _nomad.Jobs["api"] = ("running", true);
        _nomad.Jobs["new-but-rolled-back"] = ("dead", true);
        _nomad.Jobs["someone-elses"] = ("running", false);
        using var client = Client();

        var managed = await NomadLifecycle.ListManagedAsync(client, Ct);

        managed.Select(m => m.Name).Order().ShouldBe(["api", "new-but-rolled-back"]);
    }

    [Fact]
    public async Task ListManaged_Throws_WhenNomadAnswersWithAnError()
    {
        _nomad.FailListing = true;
        using var client = Client();

        await Should.ThrowAsync<System.Net.Http.HttpRequestException>(() => NomadLifecycle.ListManagedAsync(client, Ct));
    }

    [Fact]
    public async Task Delete_PurgesTheJob_AndDeletesItsVariableAndRollbackCopies()
    {
        _nomad.Jobs["api"] = ("running", true);
        _nomad.Vars.Add("nomad/jobs/api");
        _nomad.Vars.Add($"{NomadRollout.BackupRoot}/api/v3");
        _nomad.Vars.Add($"{NomadRollout.BackupRoot}/api/v4");
        _nomad.Vars.Add($"{NomadRollout.BackupRoot}/api-gateway/v1");
        _nomad.Vars.Add("nomad/jobs/worker");
        using var client = Client();

        await NomadLifecycle.DeleteServiceAsync(client, "api", new DeleteOptions(), NullLogger.Instance, Ct);

        _nomad.Requests.ShouldContain(r => r.Method == "DELETE" && r.Path == "/v1/job/api" && r.Query.Contains("purge=true"));
        _nomad.Vars.Order().ShouldBe([$"{NomadRollout.BackupRoot}/api-gateway/v1", "nomad/jobs/worker"]);
    }

    [Fact]
    public async Task Delete_WithKeepSecrets_LeavesTheVariables()
    {
        _nomad.Jobs["api"] = ("running", true);
        _nomad.Vars.Add("nomad/jobs/api");
        using var client = Client();

        await NomadLifecycle.DeleteServiceAsync(client, "api", new DeleteOptions { KeepSecrets = true }, NullLogger.Instance, Ct);

        _nomad.Vars.ShouldBe(["nomad/jobs/api"]);
    }

    [Fact]
    public async Task Delete_OfAJobThatIsAlreadyGone_IsFine()
    {
        using var client = Client();

        await NomadLifecycle.DeleteServiceAsync(client, "api", new DeleteOptions(), NullLogger.Instance, Ct);

        _nomad.Requests.ShouldNotContain(r => r.Method == "DELETE" && r.Path == "/v1/job/api");
    }

    public void Dispose() => _nomad.Dispose();

    /// <summary>Just enough of the Nomad API for the lifecycle calls.</summary>
    private sealed class FakeNomad : IDisposable
    {
        private readonly HttpListener _listener = new();

        public Dictionary<string, (string Status, bool Managed)> Jobs { get; } = [];
        public HashSet<string> Vars { get; } = [];
        public bool FailListing { get; set; }
        public List<(string Method, string Path, string Query)> Requests { get; } = [];
        public string Address { get; }

        public FakeNomad()
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

                var method = ctx.Request.HttpMethod;
                var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
                var query = ctx.Request.Url.Query;
                int status;
                string body;
                lock (this)
                {
                    Requests.Add((method, path, query));
                    (status, body) = Handle(method, path, ctx.Request.QueryString["prefix"]);
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private (int, string) Handle(string method, string path, string? prefix)
        {
            if (method == "GET" && path == "/v1/jobs")
            {
                return FailListing
                    ? (403, "Permission denied")
                    : (200, JsonSerializer.Serialize(Jobs.Select(j => new
                    {
                        ID = j.Key,
                        Status = j.Value.Status,
                        Meta = j.Value.Managed ? new Dictionary<string, string> { [NomadClient.ManagedMetaKey] = "true" } : null
                    })));
            }

            if (path.StartsWith("/v1/job/", StringComparison.Ordinal))
            {
                var job = path["/v1/job/".Length..];
                if (!Jobs.ContainsKey(job))
                {
                    return (404, "job not found");
                }

                if (method == "DELETE")
                {
                    Jobs.Remove(job);
                    return (200, "{\"EvalID\":\"e\"}");
                }

                return (200, $"{{\"ID\":\"{job}\",\"Version\":1,\"Status\":\"{Jobs[job].Status}\",\"Stop\":false}}");
            }

            if (method == "GET" && path == "/v1/vars")
            {
                return (200, JsonSerializer.Serialize(Vars.Where(v => prefix is null || v.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(v => new { Path = v })));
            }

            if (path.StartsWith("/v1/var/", StringComparison.Ordinal))
            {
                var v = path["/v1/var/".Length..];
                if (method == "DELETE")
                {
                    Vars.Remove(v);
                    return (200, "");
                }

                return Vars.Contains(v) ? (200, $"{{\"Path\":\"{v}\",\"Items\":{{\"K\":\"x\"}}}}") : (404, "variable not found");
            }

            return (404, "not found");
        }

        public void Dispose() => _listener.Close();
    }
}
