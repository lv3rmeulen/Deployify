using System.Collections.Generic;
using System.Linq;
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
    public void Bindings_DefaultStrategy(string template, string expected)
    {
        ExpressionFixtures.Resolver().Resolve(template).ToString().ShouldBe(expected);
    }

    [Fact]
    public void Bindings_UseTargetStrategy()
    {
        var resolver = ExpressionFixtures.Resolver(strategy: new EndpointStrategy
        {
            Host = res => $"{res}.prod.svc.cluster.local",
            Port = (_, _, _) => 80
        });

        resolver.Resolve("{api.bindings.http.url}").ToString().ShouldBe("http://api.prod.svc.cluster.local:80");
        resolver.Resolve("{api.bindings.http.targetPort}").ToString().ShouldBe("8080");
    }

    [Fact]
    public void Bindings_UrlOverride_IsUsedWhenItReturnsAValue()
    {
        var resolver = ExpressionFixtures.Resolver(strategy: new EndpointStrategy
        {
            Url = (res, _, scheme, _) => scheme == "http" ? $"http://{res}" : null
        });

        resolver.Resolve("{api.bindings.http.url}").ToString().ShouldBe("http://api");
        resolver.Resolve("{pg.bindings.tcp.url}").ToString().ShouldBe("tcp://pg:5432");
    }

    [Fact]
    public void Bindings_ProjectHttps_FallsBackToHttpByDefault()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.bindings.https.url}").ToString().ShouldBe("http://api:8080");
        resolver.Resolve("{api.bindings.https.targetPort}").ToString().ShouldBe("8443");
    }

    [Fact]
    public void Bindings_ProjectHttps_KeptWhenFallbackDisabled()
    {
        var resolver = ExpressionFixtures.Resolver(projectHttpsAsHttp: false);

        resolver.Resolve("{api.bindings.https.url}").ToString().ShouldBe("https://api:8443");
    }

    [Fact]
    public void Bindings_UnknownBinding_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.bindings.grpc.url}").ToString().ShouldBe("{api.bindings.grpc.url}");
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        d.Message.ShouldContain("grpc");
    }

    [Fact]
    public void Bindings_UnknownProperty_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.bindings.http.path}");

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
    }

    [Fact]
    public void Bindings_WrongShape_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.bindings.http}");

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
    }

    [Fact]
    public void Bindings_WithoutAssignedPort_IsReported()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy());

        resolver.Resolve("{api.bindings.http.url}").IsFullyResolved.ShouldBeFalse();
        resolver.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("No port");
    }

    // ── Connection strings and values ─────────────────────────────────────────

    [Fact]
    public void ConnectionString_NestsThroughBindingsAndParameters()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        var value = resolver.Resolve("{appdb.connectionString}");

        value.ToString().ShouldBe("Host=pg;Port=5432;Username=postgres;Password=s3cret;Database=appdb");
        value.IsFullyResolved.ShouldBeTrue();
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void ConnectionString_Missing_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.connectionString}");

        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        d.Message.ShouldContain("no connectionString");
    }

    [Fact]
    public void Value_ResolvesNestedBinding()
    {
        ExpressionFixtures.Resolver().Resolve("{endpoint.value}").ToString().ShouldBe("http://api:8080/v1");
    }

    [Fact]
    public void Value_Missing_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.value}");

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
    }

    [Fact]
    public void Value_OnParameterWithoutValueExpression_UsesValueInput()
    {
        var manifest = ExpressionFixtures.Parse("""
            { "resources": { "p": { "type": "parameter.v0", "inputs": { "value": { "type": "string", "default": { "value": "x" } } } } } }
            """);
        var resolver = ExpressionFixtures.Resolver(manifest, parameters: ExpressionFixtures.FixedParameters());

        resolver.Resolve("{p.value}").ToString().ShouldBe("x");
    }

    // ── Parameters and secrets ────────────────────────────────────────────────

    [Fact]
    public void Parameter_WithoutSource_StaysUnresolved()
    {
        var resolver = ExpressionFixtures.Resolver();

        var value = resolver.Resolve("{pg-password.value}");

        value.ToString().ShouldBe("{pg-password.inputs.value}");
        resolver.Unresolved.ShouldBe(new[] { "{pg-password.inputs.value}" });
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Message.ShouldContain("Parameters__pg-password");
        d.Chain.ShouldBe(new[] { "{pg-password.value}" });
    }

    [Fact]
    public void Parameter_SecretFlagFlowsIntoEveryValueThatUsesIt()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        resolver.Resolve("{pg-password.value}").IsSecret.ShouldBeTrue();
        resolver.Resolve("{pg.connectionString}").IsSecret.ShouldBeTrue();
        resolver.Resolve("{appdb.connectionString}").IsSecret.ShouldBeTrue();
        resolver.Resolve("{greeting.value}").IsSecret.ShouldBeFalse();
        resolver.Resolve("{api.bindings.http.url}").IsSecret.ShouldBeFalse();
    }

    [Fact]
    public void Parameter_PartsAllowNativeRendering()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters(("pg-password", "s3cret")));

        var value = resolver.Resolve("{appdb.connectionString}");

        var p = value.Parameters.ShouldHaveSingleItem();
        p.Parameter.ShouldBe("pg-password");
        p.Input.ShouldBe("value");
        value.Render(part => "${" + part.Parameter.ToUpperInvariant().Replace('-', '_') + "}")
            .ShouldBe("Host=pg;Port=5432;Username=postgres;Password=${PG_PASSWORD};Database=appdb");
        value.ToDisplayString().ShouldBe("Host=pg;Port=5432;Username=postgres;Password=****;Database=appdb");
    }

    [Fact]
    public void Parameter_DefaultValue_IsUsed()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        resolver.Resolve("{greeting.value}").ToString().ShouldBe("hello");
    }

    [Fact]
    public void Parameter_Generated_IsStableAcrossReferences()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        var password = resolver.Resolve("{pg-password.value}").ToString();

        password.Length.ShouldBe(22);
        resolver.Resolve("{pg.connectionString}").ToString().ShouldEndWith($"Password={password}");
    }

    [Fact]
    public void Parameter_RequiredWithoutValue_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        resolver.Resolve("{api-key.value}").IsFullyResolved.ShouldBeFalse();
        resolver.Unresolved.ShouldBe(new[] { "{api-key.inputs.value}" });
    }

    [Fact]
    public void Parameter_UnknownInput_IsReported()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        resolver.Resolve("{greeting.inputs.other}");

        resolver.Diagnostics.ShouldHaveSingleItem().Message.ShouldContain("no input named 'other'");
    }

    // ── Templates, errors and diagnostics ─────────────────────────────────────

    [Fact]
    public void Template_MixesLiteralsAndReferences()
    {
        ExpressionFixtures.Resolver()
            .Resolve("--api {api.bindings.http.host}:{api.bindings.http.port} --verbose")
            .ToString().ShouldBe("--api api:8080 --verbose");
    }

    [Fact]
    public void Template_LiteralJson_IsUntouched()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{\"retries\":3}").ToString().ShouldBe("{\"retries\":3}");
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Template_EscapedReference_IsNotResolved()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{{api.bindings.http.url}} -> {api.bindings.http.url}").ToString()
            .ShouldBe("{api.bindings.http.url} -> http://api:8080");
        resolver.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Template_NullOrEmpty_IsEmpty()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve((string?)null).ShouldBeSameAs(ResolvedValue.Empty);
        resolver.Resolve("").ToString().ShouldBe("");
    }

    [Fact]
    public void UnknownResource_IsKeptAndReported()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("x={ghost.connectionString}").ToString().ShouldBe("x={ghost.connectionString}");
        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.UnknownResource);
    }

    [Fact]
    public void UnsupportedProperty_ListsSupportedOnes()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{api.outputs.name}");

        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.UnsupportedProperty);
        d.Message.ShouldContain("bindings, connectionString, inputs, value");
    }

    [Fact]
    public void Diagnostics_AreReportedOncePerPlaceholder()
    {
        var resolver = ExpressionFixtures.Resolver();

        resolver.Resolve("{ghost.value}");
        resolver.Resolve("{ghost.value} and {ghost.value}");

        resolver.Diagnostics.Count.ShouldBe(1);
        resolver.Unresolved.Count.ShouldBe(1);
    }

    [Fact]
    public void Cycle_IsDetectedAndNamesTheChain()
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

        var value = resolver.Resolve("{a.connectionString}");

        value.ToString().ShouldBe("A[B[{a.connectionString}]]");
        var d = resolver.Diagnostics.ShouldHaveSingleItem();
        d.Kind.ShouldBe(ExpressionDiagnosticKind.Cycle);
        d.Message.ShouldContain("{a.connectionString} -> {b.connectionString} -> {a.connectionString}");
    }

    [Fact]
    public void SelfReference_IsACycle()
    {
        var manifest = ExpressionFixtures.Parse("""
            { "resources": { "a": { "type": "value.v0", "value": "x{a.value}" } } }
            """);
        var resolver = ExpressionFixtures.Resolver(manifest);

        resolver.Resolve("{a.value}");

        resolver.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.Cycle);
    }

    [Fact]
    public void DeepChain_StopsAtMaxDepth()
    {
        var resources = new Dictionary<string, AspireResource>();
        for (var i = 0; i < 10; i++)
        {
            resources[$"v{i}"] = new AspireResource { Type = "value.v0", ConnectionString = $"{{v{i + 1}.connectionString}}" };
        }

        resources["v10"] = new AspireResource { Type = "value.v0", ConnectionString = "end" };
        var manifest = new AspireManifest { Resources = resources };

        var deepEnough = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy());
        deepEnough.Resolve("{v0.connectionString}").ToString().ShouldBe("end");

        var tooShallow = new ExpressionResolver(manifest, new Dictionary<(string, string), int>(), new EndpointStrategy()) { MaxDepth = 5 };
        tooShallow.Resolve("{v0.connectionString}").IsFullyResolved.ShouldBeFalse();
        tooShallow.Diagnostics.ShouldHaveSingleItem().Kind.ShouldBe(ExpressionDiagnosticKind.DepthExceeded);
    }

    [Fact]
    public void Strict_ThrowsWithDiagnostic()
    {
        var resolver = ExpressionFixtures.Resolver(strict: true);

        var ex = Should.Throw<ExpressionException>(() => resolver.Resolve("{appdb.connectionString}"));

        ex.Kind.ShouldBe(ExpressionDiagnosticKind.MissingValue);
        var diagnostic = ex.Diagnostic.ShouldNotBeNull();
        diagnostic.Placeholder.ShouldBe("{pg-password.inputs.value}");
        diagnostic.Chain.ShouldBe(new[] { "{appdb.connectionString}", "{pg.connectionString}", "{pg-password.value}" });
    }

    [Fact]
    public void Strict_ResolvableValue_DoesNotThrow()
    {
        var resolver = ExpressionFixtures.Resolver(strict: true, parameters: ExpressionFixtures.FixedParameters(("pg-password", "pw")));

        resolver.Resolve("{appdb.connectionString}").IsFullyResolved.ShouldBeTrue();
    }

    [Fact]
    public void Resolution_IsCached()
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

        resolver.Resolve("{pg.bindings.tcp.host}");
        resolver.Resolve("{pg.connectionString}");
        resolver.Resolve("{appdb.connectionString}");

        calls.ShouldBe(1);
    }

    [Fact]
    public void ResolveReference_ByParts()
    {
        ExpressionFixtures.Resolver().ResolveReference("api", "bindings", "http", "port").ToString().ShouldBe("8080");
    }

    [Fact]
    public void ResolveAll_ResolvesEveryEnvValue()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = ExpressionFixtures.Resolver(manifest);

        var env = resolver.ResolveAll(manifest.Resources["web"].Env);

        env["services__api__http__0"].ToString().ShouldBe("http://api:8080");
        env["services__api__https__0"].ToString().ShouldBe("http://api:8080");
        env["API_ENDPOINT"].ToString().ShouldBe("http://api:8080/v1");
    }

    [Fact]
    public void Validate_CoversEnvArgsAndConnectionStrings()
    {
        var resolver = ExpressionFixtures.Resolver();

        var diagnostics = resolver.Validate();

        diagnostics.Select(d => d.Placeholder).ShouldBe(new[] { "{pg-password.inputs.value}", "{greeting.inputs.value}", "{api-key.inputs.value}" },
            ignoreOrder: true);
    }

    [Fact]
    public void Validate_WithParameters_OnlyReportsMissingRequiredOnes()
    {
        var resolver = ExpressionFixtures.Resolver(parameters: ExpressionFixtures.FixedParameters());

        resolver.Validate().ShouldHaveSingleItem().Placeholder.ShouldBe("{api-key.inputs.value}");
    }

    // ── Extensibility ─────────────────────────────────────────────────────────

    private sealed class OutputsHandler : IReferenceHandler
    {
        public string Property => "outputs";

        public ResolvedValue Resolve(ReferenceContext context)
        {
            context.RequireArguments(1, "outputs.<name>");
            return ResolvedValue.Text($"output:{context.ResourceName}:{context.Arguments[0]}");
        }
    }

    [Fact]
    public void CustomHandler_AddsAProperty()
    {
        var resolver = ExpressionFixtures.Resolver(handlers: [new OutputsHandler()]);

        resolver.Resolve("{pg.outputs.fqdn}").ToString().ShouldBe("output:pg:fqdn");
    }

    private sealed class FixedConnectionStringHandler : IReferenceHandler
    {
        public string Property => "connectionString";

        public ResolvedValue Resolve(ReferenceContext context) => ResolvedValue.Text("overridden");
    }

    [Fact]
    public void CustomHandler_CanReplaceABuiltIn()
    {
        var resolver = ExpressionFixtures.Resolver(handlers: [new FixedConnectionStringHandler()]);

        resolver.Resolve("{appdb.connectionString}").ToString().ShouldBe("overridden");
    }
}
