using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Expressions;

public sealed class ExpressionResolverTests
{
    // ── Bindings ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{api.bindings.http.url}", "http://api:8080")]
    [InlineData("{api.bindings.http.host}", "api")]
    [InlineData("{api.bindings.http.port}", "8080")]
    [InlineData("{api.bindings.http.targetPort}", "8080")]
    [InlineData("{api.bindings.http.scheme}", "http")]
    [InlineData("{pg.bindings.tcp.url}", "tcp://pg:5432")]
    public async Task Bindings_DefaultStrategy(string template, string expected)
    {
        (await ExpressionFixtures.Resolver().ResolveAsync(template, TestContext.Current.CancellationToken)).ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task Bindings_UseTargetStrategy()
    {
        var resolver = ExpressionFixtures.Resolver(strategy: new EndpointStrategy
        {
            Host = res => $"{res}.prod.svc.cluster.local",
            Port = (_, _, _) => 80
        });

        (await resolver.ResolveAsync("{api.bindings.http.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("http://api.prod.svc.cluster.local:80");
        (await resolver.ResolveAsync("{api.bindings.http.targetPort}", TestContext.Current.CancellationToken)).ToString().ShouldBe("8080");
    }

    [Fact]
    public async Task Bindings_UrlOverride_IsUsedWhenItReturnsAValue()
    {
        var resolver = ExpressionFixtures.Resolver(strategy: new EndpointStrategy
        {
            Url = (res, _, scheme, _) => scheme == "http" ? $"http://{res}" : null
        });

        (await resolver.ResolveAsync("{api.bindings.http.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("http://api");
        (await resolver.ResolveAsync("{pg.bindings.tcp.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("tcp://pg:5432");
    }

    [Fact]
    public async Task Bindings_ProjectHttps_FallsBackToHttpByDefault()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync("{api.bindings.https.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("http://api:8080");
        (await resolver.ResolveAsync("{api.bindings.https.targetPort}", TestContext.Current.CancellationToken)).ToString().ShouldBe("8443");
    }

    [Fact]
    public async Task Bindings_ProjectHttps_KeptWhenFallbackDisabled()
    {
        var resolver = ExpressionFixtures.Resolver(projectHttpsAsHttp: false);

        (await resolver.ResolveAsync("{api.bindings.https.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("https://api:8443");
    }

    [Fact]
    public async Task Bindings_UnknownBinding_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync("{api.bindings.grpc.url}", TestContext.Current.CancellationToken)).ToString().ShouldBe("{api.bindings.grpc.url}");
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        d.Message.ShouldContain("grpc");
    }

    [Fact]
    public async Task Bindings_UnknownProperty_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{api.bindings.http.path}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
    }

    [Fact]
    public async Task Bindings_WrongShape_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{api.bindings.http}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
    }

    [Fact]
    public async Task Bindings_WithoutAssignedPort_IsReported()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy());

        (await resolver.ResolveAsync("{api.bindings.http.url}", TestContext.Current.CancellationToken)).IsFullyResolved.ShouldBeFalse();
        resolver.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("No port");
    }

    // ── Connection strings and values ─────────────────────────────────────────

    [Fact]
    public async Task ConnectionString_NestsThroughBindingsAndParameters()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        var value = await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken);

        value.ToString().ShouldBe("Host=pg;Port=5432;Username=postgres;Password=s3cret;Database=appdb");
        value.IsFullyResolved.ShouldBeTrue();
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectionString_Missing_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{api.connectionString}", TestContext.Current.CancellationToken);

        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        d.Message.ShouldContain("no connectionString");
    }

    [Fact]
    public async Task Value_ResolvesNestedBinding()
    {
        (await ExpressionFixtures.Resolver().ResolveAsync("{endpoint.value}", TestContext.Current.CancellationToken)).ToString().ShouldBe("http://api:8080/v1");
    }

    [Fact]
    public async Task Value_Missing_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{api.value}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
    }

    [Fact]
    public async Task Value_OnParameterWithoutValueExpression_UsesValueInput()
    {
        var manifest = ExpressionFixtures.Parse("""
            { "resources": { "p": { "type": "parameter.v0", "inputs": { "value": { "type": "string", "default": { "value": "x" } } } } } }
            """);
        var resolver = ExpressionFixtures.Resolver(manifest, parameters: ExpressionFixtures.FixedParameters());

        (await resolver.ResolveAsync("{p.value}", TestContext.Current.CancellationToken)).ToString().ShouldBe("x");
    }

    // ── Parameters and secrets ────────────────────────────────────────────────

    [Fact]
    public async Task Parameter_WithoutSource_StaysUnresolved()
    {
        var resolver = ExpressionFixtures.Resolver();

        var value = await resolver.ResolveAsync("{pg-password.value}", TestContext.Current.CancellationToken);

        value.ToString().ShouldBe("{pg-password.inputs.value}");
        resolver.Unresolved.ShouldBe(new[] { "{pg-password.inputs.value}" });
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Message.ShouldContain("Parameters__pg-password");
        d.Chain.ShouldBe(new[] { "{pg-password.value}" });
    }

    [Fact]
    public async Task Parameter_SecretFlagFlowsIntoEveryValueThatUsesIt()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        (await resolver.ResolveAsync("{pg-password.value}", TestContext.Current.CancellationToken)).IsSecret.ShouldBeTrue();
        (await resolver.ResolveAsync("{pg.connectionString}", TestContext.Current.CancellationToken)).IsSecret.ShouldBeTrue();
        (await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken)).IsSecret.ShouldBeTrue();
        (await resolver.ResolveAsync("{greeting.value}", TestContext.Current.CancellationToken)).IsSecret.ShouldBeFalse();
        (await resolver.ResolveAsync("{api.bindings.http.url}", TestContext.Current.CancellationToken)).IsSecret.ShouldBeFalse();
    }

    [Fact]
    public async Task Parameter_PartsAllowNativeRendering()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        var value = await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken);

        var p = value.Parameters.ShouldHaveSingleItem();
        p.Parameter.ShouldBe("pg-password");
        p.Input.ShouldBe("value");
        value.Render(part => "${" + part.Parameter.ToUpperInvariant().Replace('-', '_') + "}")
            .ShouldBe("Host=pg;Port=5432;Username=postgres;Password=${PG_PASSWORD};Database=appdb");
        value.ToDisplayString().ShouldBe("Host=pg;Port=5432;Username=postgres;Password=****;Database=appdb");
    }

    [Fact]
    public async Task Parameter_DefaultValue_IsUsed()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        (await resolver.ResolveAsync("{greeting.value}", TestContext.Current.CancellationToken)).ToString().ShouldBe("hello");
    }

    [Fact]
    public async Task Parameter_Generated_IsStableAcrossReferences()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        var password = (await resolver.ResolveAsync("{pg-password.value}", TestContext.Current.CancellationToken)).ToString();

        password.Length.ShouldBe(22);
        (await resolver.ResolveAsync("{pg.connectionString}", TestContext.Current.CancellationToken)).ToString().ShouldEndWith($"Password={password}");
    }

    [Fact]
    public async Task Parameter_RequiredWithoutValue_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        (await resolver.ResolveAsync("{api-key.value}", TestContext.Current.CancellationToken)).IsFullyResolved.ShouldBeFalse();
        resolver.Unresolved.ShouldBe(new[] { "{api-key.inputs.value}" });
    }

    [Fact]
    public async Task Parameter_UnknownInput_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        await resolver.ResolveAsync("{greeting.inputs.other}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("no input named 'other'");
    }

    // ── Templates, errors and diagnostics ─────────────────────────────────────

    [Fact]
    public async Task Template_MixesLiteralsAndReferences()
    {
        (await ExpressionFixtures.Resolver()
            .ResolveAsync("--api {api.bindings.http.host}:{api.bindings.http.port} --verbose", TestContext.Current.CancellationToken))
            .ToString().ShouldBe("--api api:8080 --verbose");
    }

    [Fact]
    public async Task Template_LiteralJson_IsUntouched()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync("{\"retries\":3}", TestContext.Current.CancellationToken)).ToString().ShouldBe("{\"retries\":3}");
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Template_EscapedReference_IsNotResolved()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync("{{api.bindings.http.url}} -> {api.bindings.http.url}", TestContext.Current.CancellationToken)).ToString()
            .ShouldBe("{api.bindings.http.url} -> http://api:8080");
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Template_NullOrEmpty_IsEmpty()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync((string?)null, TestContext.Current.CancellationToken)).ShouldBeSameAs(ResolvedValue.Empty);
        (await resolver.ResolveAsync("", TestContext.Current.CancellationToken)).ToString().ShouldBe("");
    }

    [Fact]
    public async Task UnknownResource_IsKeptAndReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        (await resolver.ResolveAsync("x={ghost.connectionString}", TestContext.Current.CancellationToken)).ToString().ShouldBe("x={ghost.connectionString}");
        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnknownResource);
    }

    [Fact]
    public async Task UnsupportedProperty_ListsSupportedOnes()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{api.outputs.name}", TestContext.Current.CancellationToken);

        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
        d.Message.ShouldContain("bindings, connectionString, inputs, value");
    }

    [Fact]
    public async Task Diagnostics_AreReportedOncePerPlaceholder()
    {
        var resolver = ExpressionFixtures.Resolver();

        await resolver.ResolveAsync("{ghost.value}", TestContext.Current.CancellationToken);
        await resolver.ResolveAsync("{ghost.value} and {ghost.value}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.Count.ShouldBe(1);
        resolver.Unresolved.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cycle_IsDetectedAndNamesTheChain()
    {
        var manifest = ExpressionFixtures.Parse("""
            {
              "resources": {
                "a": { "type": "value.v0", "connectionString": "A[{b.connectionString}]" },
                "b": { "type": "value.v0", "connectionString": "B[{a.connectionString}]" }
              }
            }
            """);
        var resolver = ExpressionFixtures.Resolver(manifest);

        var value = await resolver.ResolveAsync("{a.connectionString}", TestContext.Current.CancellationToken);

        value.ToString().ShouldBe("A[B[{a.connectionString}]]");
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.Cycle);
        d.Message.ShouldContain("{a.connectionString} -> {b.connectionString} -> {a.connectionString}");
    }

    [Fact]
    public async Task SelfReference_IsACycle()
    {
        var manifest = ExpressionFixtures.Parse("""
            { "resources": { "a": { "type": "value.v0", "value": "x{a.value}" } } }
            """);
        var resolver = ExpressionFixtures.Resolver(manifest);

        await resolver.ResolveAsync("{a.value}", TestContext.Current.CancellationToken);

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.Cycle);
    }

    [Fact]
    public async Task DeepChain_StopsAtMaxDepth()
    {
        var resources = new Dictionary<string, AspireResource>();
        for (var i = 0; i < 10; i++)
        {
            resources[$"v{i}"] = new AspireResource { Type = "value.v0", ConnectionString = $"{{v{i + 1}.connectionString}}" };
        }

        resources["v10"] = new AspireResource { Type = "value.v0", ConnectionString = "end" };
        var manifest = new AspireManifest { Resources = resources };

        var deepEnough = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy());
        (await deepEnough.ResolveAsync("{v0.connectionString}", TestContext.Current.CancellationToken)).ToString().ShouldBe("end");

        var tooShallow = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy()) { MaxDepth = 5 };
        (await tooShallow.ResolveAsync("{v0.connectionString}", TestContext.Current.CancellationToken)).IsFullyResolved.ShouldBeFalse();
        tooShallow.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.DepthExceeded);
    }

    [Fact]
    public async Task Strict_ThrowsWithDiagnostic()
    {
        var resolver = ExpressionFixtures.Resolver(strict: true);

        var ex = await Should.ThrowAsync<ExpressionException>(async () => await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        var diagnostic = ex.Diagnostic.ShouldNotBeNull();
        diagnostic.Placeholder.ShouldBe("{pg-password.inputs.value}");
        diagnostic.Chain.ShouldBe(new[] { "{appdb.connectionString}", "{pg.connectionString}", "{pg-password.value}" });
    }

    [Fact]
    public async Task Strict_ResolvableValue_DoesNotThrow()
    {
        var resolver = ExpressionFixtures.Resolver(strict: true, parameters: ExpressionFixtures.FixedParameters(("pg-password", "pw")));

        (await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken)).IsFullyResolved.ShouldBeTrue();
    }

    [Fact]
    public async Task Resolution_IsCached()
    {
        var calls = 0;
        var resolver = ExpressionFixtures.Resolver(strategy: new EndpointStrategy
        {
            Host = res =>
            {
                calls++;
                return res;
            }
        });

        await resolver.ResolveAsync("{pg.bindings.tcp.host}", TestContext.Current.CancellationToken);
        await resolver.ResolveAsync("{pg.connectionString}", TestContext.Current.CancellationToken);
        await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveReference_ByParts()
    {
        (await ExpressionFixtures.Resolver().ResolveReferenceAsync("api", "bindings", "http", "port")).ToString().ShouldBe("8080");
    }

    [Fact]
    public async Task ResolveAll_ResolvesEveryEnvValue()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = ExpressionFixtures.Resolver(manifest);

        var env = await resolver.ResolveAllAsync(manifest.Resources["web"].Env, TestContext.Current.CancellationToken);

        env["services__api__http__0"].ToString().ShouldBe("http://api:8080");
        env["services__api__https__0"].ToString().ShouldBe("http://api:8080");
        env["API_ENDPOINT"].ToString().ShouldBe("http://api:8080/v1");
    }

    [Fact]
    public async Task Validate_CoversEnvArgsAndConnectionStrings()
    {
        var resolver = ExpressionFixtures.Resolver();

        var diagnostics = await resolver.ValidateAsync(TestContext.Current.CancellationToken);

        diagnostics.Select(d => d.Placeholder).ShouldBe(new[] { "{pg-password.inputs.value}", "{greeting.inputs.value}", "{api-key.inputs.value}" },
            ignoreOrder: true);
    }

    [Fact]
    public async Task Validate_WithParameters_OnlyReportsMissingRequiredOnes()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        (await resolver.ValidateAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Placeholder.ShouldBe("{api-key.inputs.value}");
    }

    // ── Extensibility ─────────────────────────────────────────────────────────

    private sealed class OutputsHandler : IReferenceHandler
    {
        public string Property => "outputs";

        public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context)
        {
            context.RequireArguments(1, "outputs.<name>");
            return ValueTask.FromResult(ResolvedValue.Text($"output:{context.ResourceName}:{context.Arguments[0]}"));
        }
    }

    [Fact]
    public async Task CustomHandler_AddsAProperty()
    {
        var resolver = ExpressionFixtures.Resolver(handlers: [new OutputsHandler()]);

        (await resolver.ResolveAsync("{pg.outputs.fqdn}", TestContext.Current.CancellationToken)).ToString().ShouldBe("output:pg:fqdn");
    }

    private sealed class FixedConnectionStringHandler : IReferenceHandler
    {
        public string Property => "connectionString";

        public ValueTask<ResolvedValue> ResolveAsync(ReferenceContext context) => ValueTask.FromResult(ResolvedValue.Text("overridden"));
    }

    [Fact]
    public async Task CustomHandler_CanReplaceABuiltIn()
    {
        var resolver = ExpressionFixtures.Resolver(handlers: [new FixedConnectionStringHandler()]);

        (await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken)).ToString().ShouldBe("overridden");
    }
}
