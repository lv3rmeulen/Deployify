using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Rollouts;
using Deployify.Core.State;
using Deployify.Target.Nomad;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>
/// adopt → apply --prune → destroy through the real CLI, against an in-process fake Nomad.
/// </summary>
public sealed class PruneDestroyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RootCommand Root() => CommandRunner.BuildFull();

    private readonly FakeNomadServer _nomad = new();
    private readonly TempDir _dir = new();

    private async Task SetUpAsync(params string[] runningJobs)
    {
        foreach (var job in runningJobs)
        {
            _nomad.Jobs[job] = true;
        }

        await Harness.WriteStateAsync(_dir.Path, new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = { ["prod"] = Harness.NomadConfig(address: _nomad.Address) }
        }, Ct);
        await Harness.WriteManifestAsync(_dir.Path, ct: Ct);
    }

    private Task<int> RunAsync(params string[] args) => Root().InvokeAsync([.. args, "--project-path", _dir.Path], Ct);

    private async Task<IReadOnlyList<string>> InventoryAsync()
    {
        await using var s = new StateSession(_dir.Path, "prod", null);
        return [.. (await s.Inventory.LoadAsync(Ct)).Services.Select(r => r.Name).Order()];
    }

    [Fact]
    public async Task ApplyWithoutPrune_LeavesARemovedService_AndApplyWithPrune_RemovesIt()
    {
        await SetUpAsync("api", "worker", "redis", "old");
        (await RunAsync("state", "adopt")).ShouldBe(0);
        (await InventoryAsync()).ShouldBe(["api", "old", "redis", "worker"]);

        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);
        _nomad.Jobs.Keys.ShouldContain("old");

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(0);

        _nomad.Jobs.Keys.Order().ShouldBe(["api", "redis", "worker"]);
        _nomad.Purged.ShouldBe(["old"]);
        (await InventoryAsync()).ShouldBe(["api", "redis", "worker"]);
        var journals = await RolloutJournal.ListAsync(_dir.Path, "prod", Ct);
        journals.ShouldContain(j => j.Record.Operation == RolloutOperation.Prune && j.Record.State == RolloutState.Succeeded);
    }

    [Fact]
    public async Task Prune_NeverTouchesAJobThatIsNotInState()
    {
        await SetUpAsync("api", "worker", "redis");
        _nomad.Jobs["someone-elses"] = true; // carries the meta key, but Deployify never recorded it here

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(0);

        _nomad.Jobs.Keys.ShouldContain("someone-elses");
        _nomad.Purged.ShouldBeEmpty();
    }

    [Fact]
    public async Task APruneOfMostServices_IsRefused_UnlessForced()
    {
        await SetUpAsync("api", "worker", "redis", "old1", "old2", "old3", "old4");
        (await RunAsync("state", "adopt")).ShouldBe(0);

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune")).ShouldBe(4);
        _nomad.Purged.ShouldBeEmpty();

        (await RunAsync("apply", "--auto-approve", "--no-health-gate", "--prune", "--force-prune")).ShouldBe(0);
        _nomad.Purged.Order().ShouldBe(["old1", "old2", "old3", "old4"]);
    }

    [Fact]
    public async Task Destroy_RemovesDependentsFirst_AndEmptiesTheInventory()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy", "--dry-run")).ShouldBe(0);
        _nomad.Purged.ShouldBeEmpty();

        (await RunAsync("destroy", "--auto-approve")).ShouldBe(0);

        _nomad.Jobs.ShouldBeEmpty();
        _nomad.Purged.IndexOf("worker").ShouldBeLessThan(_nomad.Purged.IndexOf("api")); // worker uses api
        (await InventoryAsync()).ShouldBeEmpty();
        (await RunAsync("destroy", "--auto-approve")).ShouldBe(0); // nothing left: still fine
    }

    [Fact]
    public async Task Destroy_OfOneService_LeavesTheRest()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy", "--service", "redis", "--auto-approve")).ShouldBe(0);
        (await RunAsync("destroy", "--service", "nope", "--auto-approve")).ShouldBe(1);

        _nomad.Purged.ShouldBe(["redis"]);
        (await InventoryAsync()).ShouldBe(["api", "worker"]);
    }

    [Fact]
    public async Task Destroy_WithoutATerminal_NeedsAutoApprove()
    {
        await SetUpAsync();
        (await RunAsync("apply", "--auto-approve", "--no-health-gate")).ShouldBe(0);

        (await RunAsync("destroy")).ShouldBe(1);

        _nomad.Purged.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _nomad.Dispose();
        _dir.Dispose();
    }

    /// <summary>The Nomad API calls plan, apply, adopt, prune and destroy make, in memory.</summary>
    private sealed class FakeNomadServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        /// <summary>Jobs by name; the value says whether it carries Deployify's meta key.</summary>
        public Dictionary<string, bool> Jobs { get; } = [];

        public List<string> Purged { get; } = [];
        public string Address { get; }

        public FakeNomadServer()
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

                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                int status;
                string response;
                lock (Jobs)
                {
                    try
                    {
                        (status, response) = Handle(ctx.Request.HttpMethod, Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath),
                            ctx.Request.QueryString, body);
                    }
                    catch (Exception ex)
                    {
                        // Answer instead of killing the loop, so a broken fake fails the test rather than hanging it.
                        (status, response) = (500, ex.Message);
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.StatusCode = status;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private (int, string) Handle(string method, string path, System.Collections.Specialized.NameValueCollection query, string body)
        {
            switch (method, path)
            {
                case ("GET", "/v1/agent/self"):
                    return (200, "{}");
                case ("GET", "/v1/jobs"):
                    return (200, JsonSerializer.Serialize(Jobs.Select(j => new
                    {
                        ID = j.Key,
                        Status = "running",
                        Meta = j.Value ? new Dictionary<string, string> { [NomadClient.ManagedMetaKey] = "true" } : null
                    })));
                case ("POST", "/v1/jobs/parse"):
                {
                    // NomadClient posts it with web (camelCase) JSON names.
                    var hcl = JsonDocument.Parse(body).RootElement.EnumerateObject()
                        .First(p => p.Name.Equals("JobHCL", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
                    var id = Regex.Match(hcl, "job \"([^\"]+)\"").Groups[1].Value;
                    return (200, JsonSerializer.Serialize(new { ID = id, Meta = new Dictionary<string, string> { [NomadClient.ManagedMetaKey] = "true" } }));
                }
                case ("POST", "/v1/jobs"):
                {
                    var id = JsonDocument.Parse(body).RootElement.GetProperty("Job").GetProperty("ID").GetString()!;
                    Jobs[id] = true;
                    return (200, "{\"EvalID\":\"eval-1\"}");
                }
                case ("GET", "/v1/vars"):
                    return (200, "[]");
            }

            if (path.StartsWith("/v1/var/", StringComparison.Ordinal))
            {
                return method == "GET" ? (404, "variable not found") : (200, "{}");
            }

            if (path.StartsWith("/v1/job/", StringComparison.Ordinal))
            {
                var rest = path["/v1/job/".Length..];
                if (rest.EndsWith("/plan", StringComparison.Ordinal))
                {
                    var planned = rest[..^"/plan".Length];
                    return (200, $"{{\"Diff\":{{\"Type\":\"{(Jobs.ContainsKey(planned) ? "None" : "Added")}\"}}}}");
                }

                if (!Jobs.ContainsKey(rest))
                {
                    return (404, "job not found");
                }

                if (method == "DELETE")
                {
                    Jobs.Remove(rest);
                    if (query["purge"] == "true")
                    {
                        Purged.Add(rest);
                    }

                    return (200, "{\"EvalID\":\"eval-2\"}");
                }

                return (200, $"{{\"ID\":\"{rest}\",\"Version\":1,\"Status\":\"running\",\"Stop\":false}}");
            }

            return (404, "not found");
        }

        public void Dispose() => _listener.Close();
    }
}
