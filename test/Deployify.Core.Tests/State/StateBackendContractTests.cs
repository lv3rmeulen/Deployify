using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.State;
using Deployify.Tests.Shared;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.State;

/// <summary>
/// The compare-and-swap contract every state backend has to keep, run against each of them:
/// local files for real, the remote ones against in-memory emulations of their APIs or CLIs.
/// The lock and the inventory are built on this contract alone.
/// </summary>
public abstract class StateBackendContractTests : IDisposable
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly TempDir Dir = new();

    protected abstract IStateBackend Backend { get; }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Read_OfAMissingKey_IsNull()
        => (await Backend.ReadAsync("deployify/prod/none.json", Ct)).ShouldBeNull();

    [Fact]
    public async Task Create_ThenRead_ReturnsTheDataAndTheSameTag()
    {
        var tag = await Backend.WriteAsync("deployify/prod/a.json", B("{\"v\":1}"), null, Ct);

        var blob = await Backend.ReadAsync("deployify/prod/a.json", Ct);
        blob.ShouldNotBeNull();
        Encoding.UTF8.GetString(blob.Data).ShouldBe("{\"v\":1}");
        blob.ETag.ShouldBe(tag);
    }

    [Fact]
    public async Task Create_OfAnExistingKey_IsAConflict()
    {
        await Backend.WriteAsync("deployify/prod/a.json", B("1"), null, Ct);

        await Should.ThrowAsync<StateConflictException>(() => Backend.WriteAsync("deployify/prod/a.json", B("2"), null, Ct));
    }

    [Fact]
    public async Task Write_WithTheCurrentTag_Succeeds_AndAStaleTag_IsAConflict()
    {
        var first = await Backend.WriteAsync("deployify/prod/a.json", B("1"), null, Ct);
        var second = await Backend.WriteAsync("deployify/prod/a.json", B("2"), first, Ct);

        second.ShouldNotBe(first);
        await Should.ThrowAsync<StateConflictException>(() => Backend.WriteAsync("deployify/prod/a.json", B("3"), first, Ct));
        Encoding.UTF8.GetString((await Backend.ReadAsync("deployify/prod/a.json", Ct))!.Data).ShouldBe("2");
    }

    [Fact]
    public async Task Delete_WithAStaleTag_IsAConflict_AndWithTheCurrentOne_RemovesIt()
    {
        var first = await Backend.WriteAsync("deployify/prod/a.json", B("1"), null, Ct);
        var second = await Backend.WriteAsync("deployify/prod/a.json", B("2"), first, Ct);

        await Should.ThrowAsync<StateConflictException>(() => Backend.DeleteAsync("deployify/prod/a.json", first, Ct));
        await Backend.DeleteAsync("deployify/prod/a.json", second, Ct);

        (await Backend.ReadAsync("deployify/prod/a.json", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_OfAMissingKey_IsFine_WithOrWithoutATag()
    {
        await Backend.DeleteAsync("deployify/prod/none.json", null, Ct);
        await Backend.DeleteAsync("deployify/prod/none.json", "12345", Ct);
    }

    [Fact]
    public async Task List_ReturnsTheKeysUnderAPrefix_Sorted()
    {
        await Backend.WriteAsync("deployify/prod/rollouts/b.json", B("b"), null, Ct);
        await Backend.WriteAsync("deployify/prod/rollouts/a.json", B("a"), null, Ct);
        await Backend.WriteAsync("deployify/staging/rollouts/c.json", B("c"), null, Ct);

        (await Backend.ListAsync("deployify/prod/rollouts/", Ct))
            .ShouldBe(["deployify/prod/rollouts/a.json", "deployify/prod/rollouts/b.json"]);
        (await Backend.ListAsync("deployify/none/", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLock_IsExclusive_AndFreeAgainAfterRelease()
    {
        var first = await StateLease.AcquireAsync(Backend, "deployify/prod/lock.json", "prod", "apply",
            TimeSpan.FromMinutes(3), TimeSpan.Zero, autoRenew: false, ct: Ct);

        var locked = await Should.ThrowAsync<StateLockedException>(() => StateLease.AcquireAsync(
            Backend, "deployify/prod/lock.json", "prod", "destroy", TimeSpan.FromMinutes(3), TimeSpan.Zero, autoRenew: false, ct: Ct));
        locked.Holder.Id.ShouldBe(first.Info.Id);
        locked.Message.ShouldContain("deployify state unlock " + first.Info.Id);

        await first.DisposeAsync();
        await using var second = await StateLease.AcquireAsync(Backend, "deployify/prod/lock.json", "prod", "destroy",
            TimeSpan.FromMinutes(3), TimeSpan.Zero, autoRenew: false, ct: Ct);
        second.Info.Operation.ShouldBe("destroy");
    }

    public void Dispose()
    {
        (Backend as IDisposable)?.Dispose();
        Dir.Dispose();
    }
}

public sealed class LocalStateBackendContractTests : StateBackendContractTests
{
    private readonly LocalStateBackend _backend;

    public LocalStateBackendContractTests() => _backend = LocalStateBackend.ForStateDirectory(Dir.Path);

    protected override IStateBackend Backend => _backend;

    [Fact]
    public async Task Keys_AreFilesUnderDotDeployifyState()
    {
        await _backend.WriteAsync("deployify/prod/inventory.json", [1], null, Ct);

        File.Exists(Path.Combine(Dir.Path, ".deployify", "state", "deployify", "prod", "inventory.json")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("deployify/../../x")]
    [InlineData("")]
    public async Task Keys_ThatLeaveTheRoot_AreRefused(string key)
        => await Should.ThrowAsync<ArgumentException>(() => _backend.WriteAsync(key, [1], null, Ct));
}

public sealed class ConsulStateBackendContractTests : StateBackendContractTests
{
    private readonly FakeConsulKv _consul = new();
    private readonly ConsulStateBackend _backend;

    public ConsulStateBackendContractTests()
        => _backend = new ConsulStateBackend(new ConsulStateConfig { Address = "http://consul:8500", Token = "t0ken" }, _consul.Handler);

    protected override IStateBackend Backend => _backend;

    [Fact]
    public async Task Requests_CarryTheAclToken_AndUseCas()
    {
        await _backend.WriteAsync("deployify/prod/a.json", [1], null, Ct);

        var put = _consul.Handler.Requests.First(r => r.Request.Method == HttpMethod.Put).Request;
        put.Headers.GetValues("X-Consul-Token").ShouldBe(["t0ken"]);
        put.RequestUri!.Query.ShouldBe("?cas=0");
    }
}

public sealed class AzureBlobStateBackendContractTests : StateBackendContractTests
{
    private readonly FakeBlobContainer _blobs = new();
    private readonly AzureBlobStateBackend _backend;

    public AzureBlobStateBackendContractTests()
        => _backend = new AzureBlobStateBackend(
            new AzureBlobStateConfig { AccountName = "acct", Container = "state", SasToken = "?sv=2024&sig=abc" }, _blobs.Handler);

    protected override IStateBackend Backend => _backend;

    [Fact]
    public async Task Requests_CarryTheSasToken_AndConditionalHeaders()
    {
        var tag = await _backend.WriteAsync("deployify/prod/a.json", [1], null, Ct);
        await _backend.WriteAsync("deployify/prod/a.json", [2], tag, Ct);

        var puts = _blobs.Handler.Requests.Where(r => r.Request.Method == HttpMethod.Put).Select(r => r.Request).ToList();
        puts[0].RequestUri!.ToString().ShouldBe("https://acct.blob.core.windows.net/state/deployify/prod/a.json?sv=2024&sig=abc");
        puts[0].Headers.GetValues("If-None-Match").ShouldBe(["*"]);
        puts[1].Headers.GetValues("If-Match").ShouldBe([tag]);
        puts[1].Headers.GetValues("x-ms-blob-type").ShouldBe(["BlockBlob"]);
    }

    [Fact]
    public async Task WithoutASasToken_ItUsesAnAzureCliToken()
    {
        var az = new FakeProcessRunner().When(["get-access-token"], new ProcessResult(0, "eyJ-token\n", ""));
        using var backend = new AzureBlobStateBackend(new AzureBlobStateConfig { AccountName = "acct", Container = "state" },
            _blobs.Handler, az);

        await backend.ReadAsync("deployify/prod/a.json", Ct);
        await backend.ReadAsync("deployify/prod/b.json", Ct);

        _blobs.Handler.Requests.Last().Request.Headers.Authorization!.ToString().ShouldBe("Bearer eyJ-token");
        az.Calls.Count.ShouldBe(1); // cached
        az.Calls[0].Sensitive.ShouldBeTrue();
    }

    /// <summary>The Blob REST API in memory: ETags, If-Match / If-None-Match, list with paging.</summary>
    private sealed class FakeBlobContainer
    {
        private int _version;
        private readonly Dictionary<string, (byte[] Data, string ETag)> _blobs = [];

        public FakeHttpHandler Handler { get; }

        public FakeBlobContainer() => Handler = new FakeHttpHandler(Handle);

        private HttpResponseMessage Handle(HttpRequestMessage req, string? body)
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath).TrimStart('/');
            var query = req.RequestUri.Query;
            lock (_blobs)
            {
                if (req.Method == HttpMethod.Get && query.Contains("comp=list"))
                {
                    var prefix = Uri.UnescapeDataString(query.Split("prefix=")[1].Split('&')[0]);
                    var marker = query.Contains("marker=") ? Uri.UnescapeDataString(query.Split("marker=")[1].Split('&')[0]) : null;
                    var keys = _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order().ToList();
                    // One blob per page, to exercise NextMarker.
                    var start = marker is null ? 0 : keys.IndexOf(marker);
                    var page = keys.Skip(start).Take(1).ToList();
                    var next = start + 1 < keys.Count ? keys[start + 1] : "";
                    var xml = $"<EnumerationResults><Blobs>{string.Concat(page.Select(k => $"<Blob><Name>{k}</Name></Blob>"))}</Blobs><NextMarker>{next}</NextMarker></EnumerationResults>";
                    return FakeHttpHandler.Text(HttpStatusCode.OK, xml);
                }

                var key = path["state/".Length..];
                var exists = _blobs.TryGetValue(key, out var blob);
                var ifMatch = req.Headers.TryGetValues("If-Match", out var m) ? m.First() : null;
                var ifNoneMatch = req.Headers.TryGetValues("If-None-Match", out var n) ? n.First() : null;

                if (req.Method == HttpMethod.Get)
                {
                    if (!exists)
                    {
                        return FakeHttpHandler.Text(HttpStatusCode.NotFound);
                    }

                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob.Data) };
                    ok.Headers.TryAddWithoutValidation("ETag", blob.ETag);
                    return ok;
                }

                if (req.Method == HttpMethod.Put)
                {
                    if (ifNoneMatch == "*" && exists)
                    {
                        return FakeHttpHandler.Text(HttpStatusCode.Conflict, "BlobAlreadyExists");
                    }

                    if (ifMatch is not null && (!exists || blob.ETag != ifMatch))
                    {
                        return FakeHttpHandler.Text(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                    }

                    var etag = $"\"0x8D{++_version:X}\"";
                    _blobs[key] = (Encoding.UTF8.GetBytes(body ?? ""), etag);
                    var created = new HttpResponseMessage(HttpStatusCode.Created);
                    created.Headers.TryAddWithoutValidation("ETag", etag);
                    return created;
                }

                if (req.Method == HttpMethod.Delete)
                {
                    if (!exists)
                    {
                        return FakeHttpHandler.Text(HttpStatusCode.NotFound);
                    }

                    if (ifMatch is not null && blob.ETag != ifMatch)
                    {
                        return FakeHttpHandler.Text(HttpStatusCode.PreconditionFailed);
                    }

                    _blobs.Remove(key);
                    return FakeHttpHandler.Text(HttpStatusCode.Accepted);
                }
            }

            return FakeHttpHandler.Text(HttpStatusCode.MethodNotAllowed);
        }
    }
}

public sealed class S3StateBackendContractTests : StateBackendContractTests
{
    private readonly FakeS3 _s3 = new();
    private readonly S3StateBackend _backend;

    public S3StateBackendContractTests()
        => _backend = new S3StateBackend(new S3StateConfig { Bucket = "deploy-state", Region = "eu-west-1", Profile = "ci" }, _s3.Runner);

    protected override IStateBackend Backend => _backend;

    [Fact]
    public async Task Writes_UseConditionalPuts_WithProfileAndRegion()
    {
        var tag = await _backend.WriteAsync("deployify/prod/a.json", [1], null, Ct);
        await _backend.WriteAsync("deployify/prod/a.json", [2], tag, Ct);

        var puts = _s3.Runner.Calls.Where(c => c.Args.Contains("put-object")).Select(c => c.Args.ToList()).ToList();
        puts[0][..5].ShouldBe(["--profile", "ci", "--region", "eu-west-1", "s3api"]);
        puts[0].ShouldContain("--if-none-match");
        puts[0][puts[0].IndexOf("--if-none-match") + 1].ShouldBe("*");
        puts[1][puts[1].IndexOf("--if-match") + 1].ShouldBe(tag);
    }

    [Fact]
    public async Task AnOldAwsCli_GetsAHintAboutConditionalWrites()
    {
        _s3.Runner.When(s => s.Args.Contains("put-object"), _ => new ProcessResult(252, "", "Unknown options: --if-none-match, *"));

        var ex = await Should.ThrowAsync<StateBackendException>(() => _backend.WriteAsync("deployify/prod/a.json", [1], null, Ct));
        ex.Message.ShouldContain("AWS CLI 2.22");
    }

    /// <summary><c>aws s3api</c> against an in-memory bucket.</summary>
    private sealed class FakeS3
    {
        private int _version;
        private readonly Dictionary<string, (byte[] Data, string ETag)> _objects = [];

        public FakeProcessRunner Runner { get; } = new();

        public FakeS3() => Runner.When(s => s.FileName == "aws", Handle);

        private ProcessResult Handle(ProcessSpec spec)
        {
            var a = spec.Args.ToList();
            string Arg(string name) => a[a.IndexOf(name) + 1];
            var op = a[a.IndexOf("s3api") + 1];
            lock (_objects)
            {
                switch (op)
                {
                    case "get-object":
                    {
                        var key = Arg("--key");
                        if (!_objects.TryGetValue(key, out var o))
                        {
                            return new ProcessResult(254, "", "An error occurred (NoSuchKey) when calling the GetObject operation: The specified key does not exist.");
                        }

                        File.WriteAllBytes(a[a.IndexOf("--key") + 2], o.Data);
                        return new ProcessResult(0, $$"""{"ETag":"{{o.ETag.Replace("\"", "\\\"")}}"}""", "");
                    }
                    case "put-object":
                    {
                        var key = Arg("--key");
                        var exists = _objects.TryGetValue(key, out var o);
                        if ((a.Contains("--if-none-match") && exists) || (a.Contains("--if-match") && (!exists || o.ETag != Arg("--if-match"))))
                        {
                            return new ProcessResult(254, "", "An error occurred (PreconditionFailed) when calling the PutObject operation: At least one of the pre-conditions you specified did not hold");
                        }

                        var etag = $"\"e{++_version}\"";
                        _objects[key] = (File.ReadAllBytes(Arg("--body")), etag);
                        return new ProcessResult(0, $$"""{"ETag":"{{etag.Replace("\"", "\\\"")}}"}""", "");
                    }
                    case "delete-object":
                        _objects.Remove(Arg("--key"));
                        return new ProcessResult(0, "", "");
                    case "list-objects-v2":
                    {
                        var keys = _objects.Keys.Where(k => k.StartsWith(Arg("--prefix"), StringComparison.Ordinal)).ToList();
                        return new ProcessResult(0, keys.Count == 0 ? "null" : System.Text.Json.JsonSerializer.Serialize(keys), "");
                    }
                    default:
                        return new ProcessResult(1, "", $"unexpected {op}");
                }
            }
        }
    }
}

public sealed class KubernetesStateBackendContractTests : StateBackendContractTests
{
    private readonly FakeSecrets _k8s = new();
    private readonly KubernetesStateBackend _backend;

    public KubernetesStateBackendContractTests()
        => _backend = new KubernetesStateBackend(new KubernetesStateConfig { Namespace = "deploy-state", Context = "prod" }, _k8s.Runner);

    protected override IStateBackend Backend => _backend;

    [Fact]
    public async Task Values_GoThroughStdin_AndCallsThatSeeThem_AreMarkedSensitive()
    {
        await _backend.WriteAsync("deployify/prod/parameters.json", Encoding.UTF8.GetBytes("{\"pw\":\"hunter2\"}"), null, Ct);
        await _backend.ReadAsync("deployify/prod/parameters.json", Ct);

        var create = _k8s.Runner.Single("create", "-f", "-");
        create.Args.ShouldNotContain(a => a.Contains("hunter2") || a.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"pw\":\"hunter2\"}"))));
        create.Stdin.ShouldNotBeNull();
        create.Sensitive.ShouldBeTrue();
        create.Args.Take(4).ShouldBe(["--context", "prod", "-n", "deploy-state"]);
        _k8s.Runner.Calls.Where(c => c.Args.Contains("get") && c.Args.Contains("secret")).ShouldAllBe(c => c.Sensitive);
    }

    [Fact]
    public void SecretNames_AreValidDnsNames_AndStablePerKey()
    {
        var name = KubernetesStateBackend.SecretName("deployify/prod/rollouts/20261108-120000-prod.json");

        name.ShouldMatch("^[a-z0-9-]{1,63}$");
        KubernetesStateBackend.SecretName("deployify/prod/rollouts/20261108-120000-prod.json").ShouldBe(name);
        KubernetesStateBackend.SecretName("deployify/prod/inventory.json").ShouldNotBe(name);
    }

    /// <summary>kubectl against in-memory Secrets with resourceVersions.</summary>
    private sealed class FakeSecrets
    {
        private int _rv = 100;
        private readonly Dictionary<string, JsonObject> _secrets = [];

        public FakeProcessRunner Runner { get; } = new();

        public FakeSecrets() => Runner.When(s => s.FileName == "kubectl", Handle);

        private ProcessResult Handle(ProcessSpec spec)
        {
            var a = spec.Args.ToList();
            var cmd = a[a.IndexOf("deploy-state") + 1];
            lock (_secrets)
            {
                switch (cmd)
                {
                    case "get" when a.Contains("secret"):
                        return _secrets.TryGetValue(a[a.IndexOf("secret") + 1], out var s)
                            ? new ProcessResult(0, s.ToJsonString(), "")
                            : new ProcessResult(0, "", "");
                    case "get" when a.Contains("secrets"):
                        return new ProcessResult(0, string.Concat(_secrets.Values.Select(v =>
                            v["metadata"]!["annotations"]![KubernetesStateBackend.KeyAnnotation]!.GetValue<string>() + "\n")), "");
                    case "create" or "replace":
                    {
                        var obj = JsonNode.Parse(spec.Stdin!)!.AsObject();
                        var name = obj["metadata"]!["name"]!.GetValue<string>();
                        var exists = _secrets.TryGetValue(name, out var current);
                        if (cmd == "create" && exists)
                        {
                            return new ProcessResult(1, "", $"Error from server (AlreadyExists): secrets \"{name}\" already exists");
                        }

                        if (cmd == "replace")
                        {
                            if (!exists)
                            {
                                return new ProcessResult(1, "", $"Error from server (NotFound): secrets \"{name}\" not found");
                            }

                            if (current!["metadata"]!["resourceVersion"]!.GetValue<string>() != obj["metadata"]!["resourceVersion"]!.GetValue<string>())
                            {
                                return new ProcessResult(1, "", "Error from server (Conflict): Operation cannot be fulfilled on secrets: the object has been modified");
                            }
                        }

                        obj["metadata"]!["resourceVersion"] = (++_rv).ToString();
                        _secrets[name] = obj;
                        return new ProcessResult(0, obj.ToJsonString(), "");
                    }
                    case "delete":
                        _secrets.Remove(a[a.IndexOf("secret") + 1]);
                        return new ProcessResult(0, "", "");
                    default:
                        return new ProcessResult(1, "", $"unexpected {cmd}");
                }
            }
        }
    }
}
