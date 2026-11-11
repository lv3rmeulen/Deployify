using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Deployify.Target.OvhCloud.Tests;

/// <summary>Authentication, signing and errors against the OVHcloud API.</summary>
public sealed class OvhApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OvhCredentials s_application = new("ak", "as", "ck", null, null);
    private static readonly OvhCredentials s_serviceAccount = new(null, null, null, "client1", "client-secret");

    /// <summary>A clock fixed at a given Unix time.</summary>
    private sealed class FixedTime(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    private sealed record Cluster(string Id, string Status);

    [Fact]
    public void Sign_MatchesTheOfficialAlgorithm()
    {
        // "$1$" + sha1("as+ck+GET+{url}++1700000000"), computed independently.
        OvhApiClient.Sign("as", "ck", "GET", "https://eu.api.ovh.com/1.0/cloud/project/proj1/kube/kube1", "", "1700000000")
            .ShouldBe("$1$8e6bfce8d8628280ee2554149ef0ace224285c80");
    }

    [Fact]
    public async Task ApplicationRequests_AreSigned_WithTheApiClock()
    {
        var api = FakeOvhApi.Cluster("READY");
        // Our clock is 10 seconds behind the API's (1700000000).
        using var client = new OvhApiClient("ovh-eu", s_application, api, new FixedTime(1_699_999_990));

        var cluster = await client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct);

        cluster.Status.ShouldBe("READY");
        var request = api.To("/1.0/cloud/project/proj1/kube/kube1").Single();
        request.Headers["X-Ovh-Application"].ShouldBe("ak");
        request.Headers["X-Ovh-Consumer"].ShouldBe("ck");
        request.Headers["X-Ovh-Timestamp"].ShouldBe("1700000000");
        request.Headers["X-Ovh-Signature"].ShouldBe("$1$8e6bfce8d8628280ee2554149ef0ace224285c80");
        request.Headers.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task TheClockIsAskedOnce()
    {
        var api = FakeOvhApi.Cluster("READY");
        using var client = new OvhApiClient("ovh-eu", s_application, api, new FixedTime(1_700_000_000));

        await client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct);
        await client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct);

        api.To("/1.0/auth/time").Count().ShouldBe(1);
    }

    [Fact]
    public async Task PostBodies_AreSigned()
    {
        var api = new FakeOvhApi().On(HttpMethod.Get, "/1.0/auth/time", "1700000000")
            .On(HttpMethod.Post, "/1.0/thing", """{"id":"x","status":"READY"}""");
        using var client = new OvhApiClient("ovh-eu", s_application, api, new FixedTime(1_700_000_000));

        await client.PostAsync<Cluster>("thing", new { name = "a" }, Ct);

        var request = api.To("/1.0/thing").Single();
        request.Body.ShouldBe("""{"name":"a"}""");
        request.Headers["X-Ovh-Signature"].ShouldBe(
            OvhApiClient.Sign("as", "ck", "POST", "https://eu.api.ovh.com/1.0/thing", """{"name":"a"}""", "1700000000"));
    }

    [Fact]
    public async Task ServiceAccounts_UseABearerToken_FetchedOnce()
    {
        var api = FakeOvhApi.Cluster("READY")
            .On(HttpMethod.Post, "/auth/oauth2/token", """{"access_token":"tok-1","token_type":"Bearer","expires_in":3599,"scope":"all"}""");
        using var client = new OvhApiClient("ovh-eu", s_serviceAccount, api, new FixedTime(1_700_000_000));

        await client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct);
        await client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct);

        var token = api.To("/auth/oauth2/token").Single();
        token.Uri.Host.ShouldBe("www.ovh.com");
        token.Body.ShouldContain("grant_type=client_credentials");
        token.Body.ShouldContain("client_id=client1");
        token.Body.ShouldContain("scope=all");
        api.To("/1.0/cloud/project/proj1/kube/kube1").ShouldAllBe(r => r.Headers["Authorization"] == "Bearer tok-1");
        api.To("/1.0/auth/time").ShouldBeEmpty();
    }

    [Fact]
    public async Task Errors_CarryTheApiMessage()
    {
        var api = new FakeOvhApi().On(HttpMethod.Get, "/1.0/auth/time", "1700000000")
            .On(HttpMethod.Get, "/1.0/cloud/project/proj1/kube/kube1",
                """{"class":"Client::Forbidden","message":"This call has not been granted"}""", HttpStatusCode.Forbidden);
        using var client = new OvhApiClient("ovh-eu", s_application, api, new FixedTime(1_700_000_000));

        var ex = await Should.ThrowAsync<OvhApiException>(() => client.GetAsync<Cluster>("cloud/project/proj1/kube/kube1", Ct));

        ex.Status.ShouldBe(HttpStatusCode.Forbidden);
        ex.Message.ShouldContain("This call has not been granted");
        ex.Message.ShouldContain("403");
    }

    [Theory]
    [InlineData("ovh-eu", "https://eu.api.ovh.com/1.0/", "https://www.ovh.com/auth/oauth2/token")]
    [InlineData("OVH-CA", "https://ca.api.ovh.com/1.0/", "https://ca.ovh.com/auth/oauth2/token")]
    [InlineData("ovh-us", "https://api.us.ovhcloud.com/1.0/", "https://us.ovhcloud.com/auth/oauth2/token")]
    [InlineData("https://eu.api.ovh.com/1.0", "https://eu.api.ovh.com/1.0/", "https://www.ovh.com/auth/oauth2/token")]
    public void Endpoints_MapToTheirApiAndTokenUrls(string endpoint, string api, string token)
    {
        var (apiUri, tokenUri) = OvhApiClient.ResolveEndpoint(endpoint);

        apiUri.AbsoluteUri.ShouldBe(api);
        tokenUri.AbsoluteUri.ShouldBe(token);
    }

    [Fact]
    public void AnUnknownEndpoint_IsRejected()
        => Should.Throw<InvalidOperationException>(() => OvhApiClient.ResolveEndpoint("kimsufi-eu"))
            .Message.ShouldContain("ovh-eu");

    [Fact]
    public void MissingCredentials_SayWhichVariablesToSet()
        => Should.Throw<InvalidOperationException>(() => new OvhApiClient("ovh-eu", new OvhCredentials("ak", null, null, null, null)))
            .Message.ShouldContain("OVH_APPLICATION_SECRET");

    [Fact]
    public void Credentials_NeverPrintASecret()
    {
        s_application.ToString().ShouldNotContain("as");
        s_serviceAccount.ToString().ShouldNotContain("client-secret");
    }
}
