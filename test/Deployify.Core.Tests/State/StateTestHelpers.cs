using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.Tests.State;

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public static ManualTime At2026() => new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
}

/// <summary>An HttpMessageHandler that answers from a function and records every request.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return respond(request, body);
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string text = "")
        => new(status) { Content = new StringContent(text, Encoding.UTF8) };
}

/// <summary>Consul's KV API in memory: modify indexes, ?cas= writes and deletes, ?keys listing.</summary>
internal sealed class FakeConsulKv
{
    private long _index = 10;

    public Dictionary<string, (byte[] Value, long Index)> Entries { get; } = [];

    public FakeHttpHandler Handler { get; }

    public FakeConsulKv() => Handler = new FakeHttpHandler(Handle);

    private HttpResponseMessage Handle(HttpRequestMessage req, string? body)
    {
        var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
        if (!path.StartsWith("/v1/kv/", StringComparison.Ordinal))
        {
            return FakeHttpHandler.Text(HttpStatusCode.NotFound);
        }

        var key = path["/v1/kv/".Length..];
        var query = req.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        long? cas = query.TryGetValue("cas", out var c) ? long.Parse(c) : null;

        lock (Entries)
        {
            if (req.Method == HttpMethod.Get && query.ContainsKey("keys"))
            {
                var keys = Entries.Keys.Where(k => k.StartsWith(key, StringComparison.Ordinal)).ToList();
                return keys.Count == 0
                    ? FakeHttpHandler.Text(HttpStatusCode.NotFound)
                    : FakeHttpHandler.Text(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(keys));
            }

            if (req.Method == HttpMethod.Get)
            {
                return Entries.TryGetValue(key, out var e)
                    ? FakeHttpHandler.Text(HttpStatusCode.OK,
                        $$"""[{"Key":"{{key}}","ModifyIndex":{{e.Index}},"Value":"{{Convert.ToBase64String(e.Value)}}"}]""")
                    : FakeHttpHandler.Text(HttpStatusCode.NotFound);
            }

            if (req.Method == HttpMethod.Put)
            {
                var exists = Entries.TryGetValue(key, out var e);
                if (cas is { } want && (want == 0 ? exists : !exists || e.Index != want))
                {
                    return FakeHttpHandler.Text(HttpStatusCode.OK, "false");
                }

                Entries[key] = (Encoding.UTF8.GetBytes(body ?? ""), ++_index);
                return FakeHttpHandler.Text(HttpStatusCode.OK, "true");
            }

            if (req.Method == HttpMethod.Delete)
            {
                var exists = Entries.TryGetValue(key, out var e);
                if (cas is { } want && (!exists || e.Index != want))
                {
                    return FakeHttpHandler.Text(HttpStatusCode.OK, "false");
                }

                Entries.Remove(key);
                return FakeHttpHandler.Text(HttpStatusCode.OK, "true");
            }
        }

        return FakeHttpHandler.Text(HttpStatusCode.MethodNotAllowed);
    }
}
