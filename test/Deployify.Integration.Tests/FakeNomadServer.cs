using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Target.Nomad;

namespace Deployify.Integration.Tests;

/// <summary>The Nomad API calls plan, apply, adopt, prune and destroy make, in memory.</summary>
internal sealed class FakeNomadServer : IDisposable
{
    private readonly HttpListener _listener = new();

    /// <summary>Jobs by name; the value says whether it carries Deployify's meta key.</summary>
    public Dictionary<string, bool> Jobs { get; } = [];

    public List<string> Purged { get; } = [];

    /// <summary>Jobs whose spec on the "cluster" differs from what a plan submits (changed by hand).</summary>
    public HashSet<string> Edited { get; } = [];

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
            case ("POST", "/v1/jobs"):
            {
                var id = JsonDocument.Parse(body).RootElement.GetProperty("Job").GetProperty("ID").GetString()!;
                Jobs[id] = true;
                Edited.Remove(id);
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
                var type = !Jobs.ContainsKey(planned) ? "Added" : Edited.Contains(planned) ? "Edited" : "None";
                return (200, $"{{\"Diff\":{{\"Type\":\"{type}\"}}}}");
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
