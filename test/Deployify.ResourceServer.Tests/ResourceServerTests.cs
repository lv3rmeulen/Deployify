using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aspire.DashboardService.Proto.V1;
using Deployify.Core.ResourceSources;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Xunit;
using ResourceCommandResult = Deployify.Core.ResourceSources.ResourceCommandResult;
using ResourceProperty = Deployify.Core.ResourceSources.ResourceProperty;

namespace Deployify.ResourceServer.Tests;

/// <summary>
/// The server as the Aspire dashboard sees it: a gRPC client generated from the same
/// dashboard_service.proto, talking to a real server on a free port.
/// </summary>
public sealed class ResourceServerTests : IAsyncLifetime
{
    private const string Key = "test-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeResourceSource _source = new();
    private RunningResourceServer _server = null!;
    private GrpcChannel _channel = null!;

    private static readonly ResourceSnapshot Api = new()
    {
        Name = "api",
        ResourceType = "Deployment",
        State = ResourceStates.Running,
        StateStyle = ResourceStateStyle.Success,
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
        Urls = [new ResourceUrl("http", "https://api.example.com"), new ResourceUrl("http", "http://api.ns.svc:8080", IsInternal: true)],
        Environment = [new ResourceEnvironmentVariable("LOG_LEVEL", "debug"), new ResourceEnvironmentVariable("DB_PASSWORD", null)],
        Properties = [new ResourceProperty(ResourcePropertyNames.ContainerImage, "registry/api:1.2", "Image")],
        HealthReports = [new ResourceHealthReport("replicas", ResourceHealthStatus.Degraded, "1/2 ready")],
        Commands =
        [
            ResourceCommandNames.StartCommand(enabled: false),
            ResourceCommandNames.StopCommand(enabled: true, confirmation: "Sure?")
        ]
    };

    private static readonly ResourceSnapshot ApiPod = new()
    {
        Name = "api-7d9f-abc12",
        ResourceType = "Pod",
        Parent = "api",
        State = ResourceStates.Running
    };

    public async ValueTask InitializeAsync()
    {
        _server = await ResourceServer.StartAsync(_source, new ResourceServerOptions { Port = 0, ApiKey = Key, ApplicationName = "prod" }, ct: Ct);
        _channel = GrpcChannel.ForAddress(_server.Url);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _server.DisposeAsync();
    }

    private DashboardService.DashboardServiceClient Client() => new(_channel);

    private static Metadata Auth(string key = Key) => new() { { "x-resource-service-api-key", key } };

    [Fact]
    public async Task ApplicationInformation_ReturnsTheName()
    {
        var info = await Client().GetApplicationInformationAsync(new ApplicationInformationRequest(), Auth(), cancellationToken: Ct);

        info.ApplicationName.ShouldBe("prod");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Calls_WithoutTheRightKey_AreRejected(string? key)
    {
        var headers = key is null ? new Metadata() : Auth(key);

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await Client().GetApplicationInformationAsync(new ApplicationInformationRequest(), headers, cancellationToken: Ct));

        ex.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task WatchResources_SendsTheCurrentSet_ThenChanges()
    {
        _source.Publish(Api, ApiPod);
        using var call = Client().WatchResources(new WatchResourcesRequest(), Auth(), cancellationToken: Ct);

        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        var initial = call.ResponseStream.Current.InitialData;
        initial.Resources.Select(r => r.Name).ShouldBe(["api", "api-7d9f-abc12"]); // parents first
        initial.ResourceTypes.Select(t => t.UniqueName).ShouldBe(["Deployment", "Pod"]);

        var api = initial.Resources[0];
        api.DisplayName.ShouldBe("api");
        api.State.ShouldBe("Running");
        api.StateStyle.ShouldBe("success");
        api.CreatedAt.ToDateTimeOffset().ShouldBe(Api.CreatedAt!.Value);
        api.Urls.Select(u => (u.FullUrl, u.IsInternal)).ShouldBe([("https://api.example.com", false), ("http://api.ns.svc:8080", true)]);
        api.Environment.Single(e => e.Name == "LOG_LEVEL").Value.ShouldBe("debug");
        api.Environment.Single(e => e.Name == "DB_PASSWORD").HasValue.ShouldBeFalse();
        api.Properties.Single().Value.StringValue.ShouldBe("registry/api:1.2");
        api.HealthReports.Single().Status.ShouldBe(HealthStatus.Degraded);
        api.Commands.Select(c => (c.Name, c.State)).ShouldBe(
            [("start", ResourceCommandState.Disabled), ("stop", ResourceCommandState.Enabled)]);
        api.Commands[1].ConfirmationMessage.ShouldBe("Sure?");

        var pod = initial.Resources[1];
        pod.Relationships.Single().ShouldSatisfyAllConditions(
            r => r.ResourceName.ShouldBe("api"),
            r => r.Type.ShouldBe("Parent"));

        // The pod is gone and the deployment changed: one update with both.
        _source.Publish(Api with { State = ResourceStates.Starting });
        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        var changes = call.ResponseStream.Current.Changes.Value;
        changes.Count.ShouldBe(2);
        changes[0].Upsert.State.ShouldBe("Starting");
        changes[1].Delete.ResourceName.ShouldBe("api-7d9f-abc12");
    }

    [Fact]
    public async Task WatchResources_SendsNothing_WhenASetIsUnchanged()
    {
        _source.Publish(Api);
        using var call = Client().WatchResources(new WatchResourcesRequest(), Auth(), cancellationToken: Ct);
        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();

        // A poll that finds the same thing (new list objects, same content), then a real change.
        _source.Publish(Api with { Urls = [.. Api.Urls], Commands = [.. Api.Commands] });
        _source.Publish(Api, ApiPod);

        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        call.ResponseStream.Current.Changes.Value.Single().Upsert.Name.ShouldBe("api-7d9f-abc12");
    }

    [Fact]
    public async Task ASecondDashboard_GetsTheSameState()
    {
        _source.Publish(Api, ApiPod);
        using var first = Client().WatchResources(new WatchResourcesRequest(), Auth(), cancellationToken: Ct);
        (await first.ResponseStream.MoveNext(Ct)).ShouldBeTrue();

        using var second = Client().WatchResources(new WatchResourcesRequest { IsReconnect = true }, Auth(), cancellationToken: Ct);
        (await second.ResponseStream.MoveNext(Ct)).ShouldBeTrue();

        second.ResponseStream.Current.InitialData.Resources.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConsoleLogs_AreNumberedFromOne_AndEndWithoutFollow()
    {
        _source.Logs["api"] = [new("starting"), new("oops", IsStdErr: true), new("listening")];

        using var call = Client().WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest { ResourceName = "api", SuppressFollow = true }, Auth(), cancellationToken: Ct);
        var lines = new List<ConsoleLogLine>();
        while (await call.ResponseStream.MoveNext(Ct))
        {
            lines.AddRange(call.ResponseStream.Current.LogLines);
        }

        lines.Select(l => (l.LineNumber, l.Text, l.IsStdErr)).ShouldBe(
            [(1, "starting", false), (2, "oops", true), (3, "listening", false)]);
    }

    [Fact]
    public async Task ConsoleLogs_KeepStreaming_WhileFollowing()
    {
        _source.Logs["api"] = [new("old")];

        using var call = Client().WatchResourceConsoleLogs(
            new WatchResourceConsoleLogsRequest { ResourceName = "api" }, Auth(), cancellationToken: Ct);
        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        call.ResponseStream.Current.LogLines.Single().Text.ShouldBe("old");

        _source.LiveLogs.Writer.TryWrite(new ResourceLogLine("new"));
        (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
        call.ResponseStream.Current.LogLines.Single().ShouldSatisfyAllConditions(
            l => l.Text.ShouldBe("new"),
            l => l.LineNumber.ShouldBe(2));
    }

    [Fact]
    public async Task Commands_RunOnTheSource_AndAskForARefresh()
    {
        var response = await Client().ExecuteResourceCommandAsync(
            new ResourceCommandRequest { ResourceName = "api", CommandName = "stop", ResourceType = "Deployment" },
            Auth(), cancellationToken: Ct);

        response.Kind.ShouldBe(ResourceCommandResponseKind.Succeeded);
        response.Message.ShouldBe("done");
        _source.Commands.ShouldBe([("api", "stop")]);
        _source.RefreshRequests.ShouldBe(1);
    }

    [Fact]
    public async Task FailedCommands_ReportTheirMessage()
    {
        _source.CommandResult = ResourceCommandResult.Failed("kubectl said no");

        var response = await Client().ExecuteResourceCommandAsync(
            new ResourceCommandRequest { ResourceName = "api", CommandName = "restart" }, Auth(), cancellationToken: Ct);

        response.Kind.ShouldBe(ResourceCommandResponseKind.Failed);
        response.Message.ShouldBe("kubectl said no");
    }

    [Fact]
    public async Task Interactions_AreUnimplemented_WhichTheDashboardTreatsAsUnsupported()
    {
        using var call = Client().WatchInteractions(Auth(), cancellationToken: Ct);

        var ex = await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(Ct));

        ex.StatusCode.ShouldBe(StatusCode.Unimplemented);
    }
}

/// <summary>A server without an API key accepts every caller.</summary>
public sealed class UnsecuredResourceServerTests
{
    [Fact]
    public async Task Calls_WithoutAKey_AreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ResourceServer.StartAsync(new FakeResourceSource(), new ResourceServerOptions { Port = 0 }, ct: ct);
        using var channel = GrpcChannel.ForAddress(server.Url);

        var info = await new DashboardService.DashboardServiceClient(channel)
            .GetApplicationInformationAsync(new ApplicationInformationRequest(), cancellationToken: ct);

        info.ApplicationName.ShouldBe("Deployify");
    }
}
