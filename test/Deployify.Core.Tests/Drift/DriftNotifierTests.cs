using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Drift;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Drift;

/// <summary>What alerts say, and how they are posted to Slack, Teams and webhooks.</summary>
public sealed class DriftNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset s_t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static DriftAlert Alert(DriftEvent e = DriftEvent.Drifted, params DriftEntry[] entries) => new(e, new DriftCheck
    {
        Environment = "prod",
        Status = e == DriftEvent.Resolved ? DriftStatus.InSync : DriftStatus.Drifted,
        CheckedAt = s_t0,
        TargetId = "Kubernetes",
        TargetName = "Kubernetes",
        Entries = entries.Length > 0 ? entries :
        [
            new DriftEntry("redis", DriftKind.Missing, null, null, null, null),
            new DriftEntry("api", DriftKind.Changed, "replicas", "5", "2", null)
        ],
        Baseline = new DeployedBaseline
        {
            Environment = "prod",
            At = s_t0.AddDays(-2),
            Target = "Kubernetes",
            ReleaseId = "a1b2c3d4e5f6",
            Tag = "4f2c9e1",
            Commit = "4f2c9e1a7b3d5e6f7a8b",
            Actor = "lv3rmeulen",
            Config = new(),
            Manifest = new()
        }
    }, s_t0.AddHours(-3), DriftStatus.InSync, "https://github.com/acme/shop/actions/runs/42");

    [Fact]
    public void Title_CountsWhatDrifted()
    {
        DriftMessages.Title(Alert()).ShouldBe("prod drifted from its last deploy: 1 changed, 1 missing");
        DriftMessages.Title(Alert(DriftEvent.Resolved) with { PreviousStatus = DriftStatus.Drifted }).ShouldBe("prod matches its last deploy again");
    }

    [Fact]
    public void Lines_SayWhatHappenedToEachService()
    {
        DriftMessages.Lines(Alert().Check).ShouldBe(
        [
            "- redis: deployed, but no longer on the target",
            "~ api: replicas changed (live 5, deployed 2)"
        ]);
    }

    [Fact]
    public void Lines_LeaveOutRenderedDiffs()
    {
        var alert = Alert(DriftEvent.Drifted, new DriftEntry("api", DriftKind.Changed, "manifest", null, "-  replicas: 5\n+  replicas: 2", null));
        DriftMessages.Lines(alert.Check).ShouldBe(["~ api: manifest changed"]);
    }

    [Fact]
    public void Slack_HasTheTitleAsFallback_AndEscapesText()
    {
        var alert = Alert(DriftEvent.Drifted, new DriftEntry("a<b>", DriftKind.Extra, null, null, null, null));
        var payload = DriftMessages.Slack(alert);

        payload["text"]!.GetValue<string>().ShouldBe("prod drifted from its last deploy: 1 extra");
        var section = payload["blocks"]![0]!["text"]!["text"]!.GetValue<string>();
        section.ShouldContain("a&lt;b&gt;");
        section.ShouldNotContain("<b>");
        payload["blocks"]![1]!["elements"]!.AsArray().Select(e => e!["text"]!.GetValue<string>())
            .ShouldContain("<https://github.com/acme/shop/actions/runs/42|Drift check run>");
    }

    [Fact]
    public void Slack_ListsAtMostTenDifferences()
    {
        var entries = Enumerable.Range(0, 14).Select(i => new DriftEntry($"svc{i:00}", DriftKind.Missing, null, null, null, null)).ToArray();
        var text = DriftMessages.Slack(Alert(DriftEvent.Drifted, entries))["blocks"]![0]!["text"]!["text"]!.GetValue<string>();

        text.Split('\n').Count(l => l.StartsWith("• -", StringComparison.Ordinal)).ShouldBe(DriftMessages.MaxListed);
        text.ShouldContain("and 4 more");
    }

    [Fact]
    public void Teams_IsAnAdaptiveCardMessage()
    {
        var payload = DriftMessages.Teams(Alert());

        payload["type"]!.GetValue<string>().ShouldBe("message");
        var attachment = payload["attachments"]![0]!;
        attachment["contentType"]!.GetValue<string>().ShouldBe("application/vnd.microsoft.card.adaptive");
        var card = attachment["content"]!;
        card["type"]!.GetValue<string>().ShouldBe("AdaptiveCard");
        card["body"]![0]!["text"]!.GetValue<string>().ShouldStartWith("prod drifted");
        card["body"]!.AsArray().Last()!["type"]!.GetValue<string>().ShouldBe("FactSet");
        card["actions"]![0]!["url"]!.GetValue<string>().ShouldBe("https://github.com/acme/shop/actions/runs/42");
    }

    [Fact]
    public void Webhook_CarriesEverything()
    {
        var payload = DriftMessages.Webhook(Alert());

        payload["source"]!.GetValue<string>().ShouldBe("deployify");
        payload["event"]!.GetValue<string>().ShouldBe("drifted");
        payload["status"]!.GetValue<string>().ShouldBe("drifted");
        payload["previousStatus"]!.GetValue<string>().ShouldBe("inSync");
        payload["deployed"]!["commit"]!.GetValue<string>().ShouldBe("4f2c9e1a7b3d5e6f7a8b");
        var api = payload["entries"]!.AsArray().Single(e => e!["service"]!.GetValue<string>() == "api")!;
        api["kind"]!.GetValue<string>().ShouldBe("changed");
        api["live"]!.GetValue<string>().ShouldBe("5");
        api["deployed"]!.GetValue<string>().ShouldBe("2");
    }

    [Fact]
    public async Task Notifier_ReadsTheUrlAndHeadersFromTheEnvironment()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var variables = new Dictionary<string, string> { ["DRIFT_HOOK"] = "https://hooks.example.test/abc", ["DRIFT_TOKEN"] = "s3cret" };
        var notifier = new HttpDriftNotifier(new HttpClient(handler), n => variables.GetValueOrDefault(n));

        var delivery = await notifier.SendAsync(new DriftChannel
        {
            Type = DriftChannelType.Webhook,
            Url = "env:DRIFT_HOOK",
            Headers = { ["Authorization"] = "env:DRIFT_TOKEN" },
            Name = "pager"
        }, Alert(), Ct);

        delivery.ShouldBe(new DriftDelivery("pager", DriftChannelType.Webhook, true, null));
        handler.Request!.RequestUri.ShouldBe(new Uri("https://hooks.example.test/abc"));
        handler.Request.Headers.GetValues("Authorization").ShouldBe(["s3cret"]);
        JsonNode.Parse(handler.Body!)!["event"]!.GetValue<string>().ShouldBe("drifted");
    }

    [Fact]
    public async Task Notifier_ReportsFailures_WithoutTheUrl()
    {
        var notifier = new HttpDriftNotifier(new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden, "invalid_token")), _ => null);

        var failed = await notifier.SendAsync(new DriftChannel { Type = DriftChannelType.Slack, Url = "https://hooks.slack.test/T0/B0/secret" }, Alert(), Ct);
        failed.Ok.ShouldBeFalse();
        failed.Error.ShouldBe("HTTP 403 Forbidden: invalid_token");

        var unset = await notifier.SendAsync(new DriftChannel { Type = DriftChannelType.Slack, Url = "env:NOPE" }, Alert(), Ct);
        unset.Ok.ShouldBeFalse();
        unset.Error!.ShouldContain("NOPE");
        unset.Error!.ShouldContain("not set");
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
