using System.Threading.Tasks;
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
    public async Task ResolveAll_ResolvesBindingsAndKeepsLiterals()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = Create();

        var env = await resolver.ResolveAllAsync(manifest.Resources["api"].Env, TestContext.Current.CancellationToken);

        env["JSON"].ShouldBe("{\"retries\":3}");
        env["GREETING"].ShouldBe("{greeting.inputs.value}");
        env["ConnectionStrings__appdb"].ShouldBe(
            "Host=pg;Port=5432;Username=postgres;Password={pg-password.inputs.value};Database=appdb");
    }

    [Fact]
    public async Task ResolveAll_Null_IsEmpty()
    {
        (await Create().ResolveAllAsync(null, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unresolved_ListsParameterInputsWhenNoSourceIsGiven()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = Create();

        await resolver.ResolveAllAsync(manifest.Resources["api"].Env, TestContext.Current.CancellationToken);

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
    public async Task WithParameterSource_ResolvesParameters()
    {
        var resolver = Create(ExpressionFixtures.FixedParameters(("pg-password", "pw"), ("api-key", "k")));

        (await resolver.ResolveAsync("{appdb.connectionString}", TestContext.Current.CancellationToken))
            .ShouldBe("Host=pg;Port=5432;Username=postgres;Password=pw;Database=appdb");
        (await resolver.ResolveAsync("{api-key.value}", TestContext.Current.CancellationToken)).ShouldBe("k");
        resolver.Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProjectHttpsAsHttp_CanBeTurnedOff()
    {
        var manifest = ExpressionFixtures.AppManifest();
        var resolver = new PlaceholderResolver(manifest, ServicePorts.Assign(manifest), new EndpointStrategy())
        {
            ProjectHttpsAsHttp = false
        };

        (await resolver.ResolveAsync("{api.bindings.https.url}", TestContext.Current.CancellationToken)).ShouldBe("https://api:8443");
        resolver.Expressions.ProjectHttpsAsHttp.ShouldBeFalse();
    }

    [Fact]
    public async Task Strategy_IsApplied()
    {
        var resolver = Create(strategy: new EndpointStrategy { Host = _ => "localhost" });

        (await resolver.ResolveAsync("{api.bindings.http.url}", TestContext.Current.CancellationToken)).ShouldBe("http://localhost:8080");
    }
}
