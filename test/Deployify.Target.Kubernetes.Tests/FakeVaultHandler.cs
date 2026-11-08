using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>Records Vault API requests and replies with canned responses.</summary>
internal sealed class FakeVaultHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; }
        = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
