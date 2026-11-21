using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

/// <summary><c>deployify logs</c> on App Platform: the component's run logs, through the URLs the API hands out.</summary>
public sealed class DoLogsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeDigitalOceanApi _api = new();

    public DoLogsTests()
    {
        var spec = JsonNode.Parse("""{ "name": "deployify-prod", "services": [ { "name": "api" } ], "workers": [ { "name": "worker" } ] }""")!.AsObject();
        _api.Apps["app-1"] = new DoApp("app-1", "deployify-prod", spec, null, null);
    }

    private DoDeploymentTarget Target() => new(Substitute.For<IPublisher>(), NullLogger<DoDeploymentTarget>.Instance)
    {
        ApiFactory = _ => _api,
        GetVariable = name => name == "DIGITALOCEAN_ACCESS_TOKEN" ? "dop_v1_test" : null
    };

    [Fact]
    public async Task ReadsTheComponentsLiveUrl_AndParsesItsLines()
    {
        _api.LogUrls = new DoLogUrls("https://logs.example/live?token=x", ["https://logs.example/old"]);
        _api.LogContent["https://logs.example/live?token=x"] =
        [
            "api 2026-11-21T10:00:00.123456789Z Now listening",
            "api api-6f9c-abcde 2026-11-21T10:00:01Z from an instance",
            "no prefix at all"
        ];

        var lines = await Target().ReadLogsAsync(DoFixtures.Config(), new ServiceLogQuery { Service = "api", Tail = 20 }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Instance, l.Text)).ShouldBe(
        [
            (null, "Now listening"), ("api-6f9c-abcde", "from an instance"), (null, "no prefix at all")
        ]);
        lines[0].Timestamp.ShouldNotBeNull();
        _api.Calls.ShouldContain("logs app-1/api follow=False tail=20");
        _api.Calls.ShouldNotContain("read https://logs.example/old");
    }

    [Fact]
    public async Task WithoutALiveUrl_TheArchivedLogsAreRead_UnlessFollowing()
    {
        _api.LogUrls = new DoLogUrls(null, ["https://logs.example/a", "https://logs.example/b"]);
        _api.LogContent["https://logs.example/a"] = ["worker 2026-11-21T09:00:00Z a"];
        _api.LogContent["https://logs.example/b"] = ["worker 2026-11-21T09:00:01Z b"];

        (await Target().ReadLogsAsync(DoFixtures.Config(), new ServiceLogQuery { Service = "worker" }, Ct).ToListAsync(Ct))
            .Select(l => l.Text).ShouldBe(["a", "b"]);
        (await Target().ReadLogsAsync(DoFixtures.Config(), new ServiceLogQuery { Service = "worker", Follow = true }, Ct).ToListAsync(Ct))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task AServiceThatIsNotAComponent_SaysSo()
    {
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await Target().ReadLogsAsync(DoFixtures.Config(), new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no component 'nope'");
    }

    [Theory]
    [InlineData("{\"data\":\"one\\ntwo\\n\"}", new[] { "one", "two" })]
    [InlineData("plain line", new[] { "plain line" })]
    public void WebSocketMessages_AreSplitIntoLines(string message, string[] expected)
        => DigitalOceanApi.LogMessageLines(message).ShouldBe(expected);
}
