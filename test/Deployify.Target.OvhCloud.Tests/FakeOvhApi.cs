using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;

namespace Deployify.Target.OvhCloud.Tests;

/// <summary>
/// Stands in for the OVHcloud API: answers by method and path, and records every request with
/// its headers and body so tests can check authentication and signing.
/// </summary>
internal sealed class FakeOvhApi : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpStatusCode> Status, Func<string> Body)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public FakeOvhApi On(HttpMethod method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
        => On(method, path, () => body, status);

    public FakeOvhApi On(HttpMethod method, string path, Func<string> body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method, path, () => status, body));
        return this;
    }

    /// <summary>A cluster with the given status, and the endpoints an API application uses.</summary>
    public static FakeOvhApi Cluster(params string[] statuses)
    {
        var queue = new Queue<string>(statuses);
        var last = statuses[^1];
        return new FakeOvhApi()
            .On(HttpMethod.Get, "/1.0/auth/time", "1700000000")
            .On(HttpMethod.Get, "/1.0/cloud/project/proj1/kube/kube1", () => ClusterJson(queue.Count > 0 ? queue.Dequeue() : last))
            .On(HttpMethod.Post, "/1.0/cloud/project/proj1/kube/kube1/kubeconfig",
                JsonSerializer.Serialize(new { content = Kubeconfig }));
    }

    public const string Kubeconfig = "apiVersion: v1\nkind: Config\ncurrent-context: kubernetes-admin@demo\n";

    public static string ClusterJson(string status) => JsonSerializer.Serialize(new
    {
        id = "kube1", name = "demo", region = "GRA7", status, version = "1.31", url = "abc.c1.gra7.k8s.ovh.net"
    });

    public IEnumerable<RecordedRequest> To(string path) => Requests.Where(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.RequestUri!.AbsolutePath, headers, body));

        foreach (var route in Enumerable.Reverse(_routes))
        {
            if (route.Method == request.Method && route.Path == request.RequestUri.AbsolutePath)
            {
                return new HttpResponseMessage(route.Status())
                {
                    Content = new StringContent(route.Body(), Encoding.UTF8, "application/json")
                };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"class":"Client::NotFound","message":"This call has not been granted"}""")
        };
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

internal static class Fixtures
{
    public static AspireManifest Manifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { ContainerPort = 8080, Protocol = "http", IsExternal = true }
                }
            },
            ["redis"] = new()
            {
                Type = "container.v0",
                Image = "redis:7",
                Bindings = new Dictionary<string, AspireBinding> { ["tcp"] = new() { ContainerPort = 6379, Protocol = "tcp" } }
            }
        }
    };

    /// <summary>An OVHcloud environment with literal application credentials (tests only).</summary>
    public static EnvironmentConfig Config(string kubeconfigPath, object? ovhcloud = null, object? kubernetes = null)
    {
        var config = new EnvironmentConfig
        {
            Target = DeploymentTargetType.OvhCloud,
            ContainerRegistry = "abc.gra7.container-registry.ovh.net/myapp",
            ImageTag = "v1"
        };
        config.TargetConfig["kubernetes"] = Element(kubernetes ?? new { namespacePrefix = "test" });
        config.TargetConfig["ovhcloud"] = Element(ovhcloud ?? new
        {
            endpoint = "ovh-eu", projectId = "proj1", clusterId = "kube1",
            applicationKey = "ak", applicationSecret = "as", consumerKey = "ck",
            kubeconfigPath
        });
        return config;
    }

    public static JsonElement Element(object value) => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployify-ovh-tests-" + System.IO.Path.GetRandomFileName());

    public TempDir() => System.IO.Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (System.IO.IOException)
        {
        }
    }
}
