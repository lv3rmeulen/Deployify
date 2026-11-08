using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>Plan and diff against a fake Nomad HTTP API.</summary>
public sealed class NomadPlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NomadDeploymentTarget Target()
        => new(new JobGenerator(NullLogger<JobGenerator>.Instance), Substitute.For<IPublisher>(),
            NullLogger<NomadDeploymentTarget>.Instance, NullLoggerFactory.Instance);

    [Fact]
    public async Task Plan_JobWhosePlanCallFails_IsUnknownWithReason_NotUpdate()
    {
        using var nomad = new FakeNomad(planStatus: 500, planBody: "rpc error: permission denied");
        using var dir = new TempDir();

        var plan = await Target().PlanAsync(Fixtures.SingleService("api"), Fixtures.NomadConfig(nomad.Address), dir.Path, Ct);

        plan.ToUpdate.ShouldBeEmpty();
        plan.Unchanged.ShouldBeEmpty();
        plan.Unknown.ShouldHaveSingleItem().ServiceName.ShouldBe("api");
        plan.Unknown[0].Reason.ShouldContain("permission denied");
        plan.IsUpToDate.ShouldBeFalse();
    }

    [Theory]
    [InlineData("None", DiffKind.Unchanged)]
    [InlineData("Edited", DiffKind.Changed)]
    public async Task PlanAndDiff_UseNomadsDiffType_AndAgree(string diffType, DiffKind expected)
    {
        using var nomad = new FakeNomad(planBody: $"{{\"Diff\":{{\"Type\":\"{diffType}\"}}}}");
        using var dir = new TempDir();
        var config = Fixtures.NomadConfig(nomad.Address);

        var diff = await Target().DiffAsync(Fixtures.SingleService("api"), config, dir.Path, Ct);
        var plan = await Target().PlanAsync(Fixtures.SingleService("api"), config, dir.Path, Ct);

        diff.ShouldHaveSingleItem().Kind.ShouldBe(expected);
        (expected == DiffKind.Unchanged ? plan.Unchanged : plan.ToUpdate).ShouldBe(["api"]);
    }

    [Fact]
    public async Task Plan_ManagedJobMissingFromManifest_IsDeleted_NewServiceIsCreated()
    {
        using var nomad = new FakeNomad(runningJob: "old");
        using var dir = new TempDir();

        var plan = await Target().PlanAsync(Fixtures.SingleService("api"), Fixtures.NomadConfig(nomad.Address), dir.Path, Ct);

        plan.ToCreate.ShouldBe(["api"]);
        plan.ToDelete.ShouldBe(["old"]);
    }

    /// <summary>Just enough of the Nomad API for plan: list jobs, parse HCL, plan a job.</summary>
    private sealed class FakeNomad : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeNomad(string runningJob = "api", int planStatus = 200, string planBody = "{\"Diff\":{\"Type\":\"None\"}}")
        {
            var port = FreePort();
            Address = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = Task.Run(async () =>
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

                    var path = ctx.Request.Url!.AbsolutePath;
                    var (status, body) = path switch
                    {
                        "/v1/jobs" when ctx.Request.HttpMethod == "GET" =>
                            (200, $"[{{\"ID\":\"{runningJob}\",\"Status\":\"running\",\"Meta\":{{\"deployify-managed\":\"true\"}}}}]"),
                        "/v1/jobs/parse" => (200, "{\"ID\":\"api\",\"Name\":\"api\"}"),
                        "/v1/job/api/plan" => (planStatus, planBody),
                        _ => (404, "not found")
                    };
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public string Address { get; }

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
