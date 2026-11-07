using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Expressions;

/// <summary>
/// <see cref="PlaceholderResolver"/> is the string facade every target generator uses; these
/// pin down that it behaves as it did before it was moved onto <see cref="ExpressionResolver"/>.
/// </summary>
public sealed class PlaceholderResolverTests
{
    private static PlaceholderResolver Create(IParameterSource? parameters = null, EndpointStrategy? strategy = null)
    {
        var manifest = ExpressionFixtures.AppManifest();
        return new PlaceholderResolver(manifest, ServicePorts.Assign(manifest), strategy ?? new EndpointStrategy(), parameters);
    }

    [Fact]
    public void ResolveAll_ResolvesBindingsAndKeepsLiterals()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = Create();

        var env = resolver.ResolveAll(manifest.Resources["api"].Env);

        env["JSON"].ShouldBe("{\"retries\":3}");
        env["GREETING"].ShouldBe("{greeting.inputs.value}");
        env["ConnectionStrings__appdb"].ShouldBe(
            "Host=pg;Port=5432;Username=postgres;Password={pg-password.inputs.value};Database=appdb");
    }

    [Fact]
    public void ResolveAll_Null_IsEmpty()
    {
        Create().ResolveAll(null).ShouldBeEmpty();
    }

    [Fact]
    public void Unresolved_ListsParameterInputsWhenNoSourceIsGiven()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = Create();

        resolver.ResolveAll(manifest.Resources["api"].Env);

        resolver.Unresolved.ShouldBe(new[] { "{pg-password.inputs.value}", "{greeting.inputs.value}", "{api-key.inputs.value}" },
            ignoreOrder: true);
        resolver.Diagnostics.Count.ShouldBe(3);
    }

    [Fact]
    public void Unresolved_IsEmptyBeforeAnyResolve()
    {
        Create().Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public void WithParameterSource_ResolvesParameters()
    {
        var resolver = Create(ExpressionFixtures.FixedParameters(("pg-password", "pw"), ("api-key", "k")));

        resolver.Resolve("{appdb.connectionString}")
            .ShouldBe("Host=pg;Port=5432;Username=postgres;Password=pw;Database=appdb");
        resolver.Resolve("{api-key.value}").ShouldBe("k");
        resolver.Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public void ProjectHttpsAsHttp_CanBeTurnedOff()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = new PlaceholderResolver(manifest, ServicePorts.Assign(manifest), new EndpointStrategy())
        {
            ProjectHttpsAsHttp = false
        };

        resolver.Resolve("{api.bindings.https.url}").ShouldBe("https://api:8443");
        resolver.Expressions.ProjectHttpsAsHttp.ShouldBeFalse();
    }

    [Fact]
    public void Strategy_IsApplied()
    {
        var resolver = Create(strategy: new EndpointStrategy { Host = _ => "localhost" });

        resolver.Resolve("{api.bindings.http.url}").ShouldBe("http://localhost:8080");
    }
}
