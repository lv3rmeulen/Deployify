using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

/// <summary>Ownership tags, and what destroy and prune do on Azure Container Apps.</summary>
public sealed class AcaLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AcaDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<AcaDeploymentTarget>.Instance) { Runner = fake };

    private static ProcessResult Ok(string json) => new(0, json, "");

    private static EnvironmentConfig Prod()
    {
        var config = AcaFixtures.AcaConfig();
        config.Name = "prod";
        return config;
    }

    private static string App(string name, string? tags)
        => $$"""{"name":"{{name}}","id":"/subscriptions/s/resourceGroups/test-rg/providers/Microsoft.App/containerApps/{{name}}","tags":{{tags ?? "null"}}}""";

    private const string Mine = """{"deployify-managed-by":"deployify","deployify-env":"prod"}""";

    [Fact]
    public async Task Deploy_TagsTheApp_WithoutTouchingItsTemplate()
    {
        using var dir = new AcaFixtures.TempDir();
        var fake = new FakeProcessRunner()
            .When(["containerapp", "list"], Ok("[]"))
            .When(["containerapp", "create"], Ok("""{"id":"/subscriptions/s/resourceGroups/test-rg/providers/Microsoft.App/containerApps/myapp"}"""));
        var cfg = new AcaTargetConfig { ResourceGroup = "test-rg", DeployifyEnvironment = "prod" };
        var spec = System.IO.Path.Combine(dir.Path, "myapp.json");
        await System.IO.File.WriteAllTextAsync(spec, "{}", Ct);

        await new AcaCliClient(cfg, NullLogger.Instance, fake).DeployAppAsync(spec, "myapp", Ct);

        var tag = fake.Single("resource", "tag").Args.ToList();
        tag[tag.IndexOf("--ids") + 1].ShouldEndWith("/containerApps/myapp");
        tag.ShouldContain("--is-incremental");
        tag.ShouldContain("deployify-managed-by=deployify");
        tag.ShouldContain("deployify-env=prod");
    }

    [Fact]
    public async Task ListManaged_ReturnsOnlyTaggedApps()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "list"],
            Ok($"[{App("api", Mine)},{App("hand-made", null)},{App("web", """{"deployify-managed-by":"deployify","deployify-env":"staging"}""")},{App("other", """{"team":"x"}""")}]"));

        var managed = await Target(fake).ListManagedAsync(Prod(), Ct);

        managed.Select(m => (m.Name, m.Environment)).ShouldBe([("api", "prod"), ("web", "staging")]);
    }

    [Fact]
    public async Task ListManaged_Throws_WhenAzureCantBeAsked()
    {
        var fake = new FakeProcessRunner().When(["containerapp", "list"], new ProcessResult(1, "", "ERROR: Please run 'az login'"));

        await Should.ThrowAsync<InvalidOperationException>(() => Target(fake).ListManagedAsync(Prod(), Ct));
    }

    [Fact]
    public async Task Diff_ListsOnlyTaggedAppsOfThisEnvironment_AsRemoved()
    {
        using var dir = new AcaFixtures.TempDir();
        var fake = new FakeProcessRunner().When(["containerapp", "list"],
            Ok($"[{App("myapp", Mine)},{App("gone", Mine)},{App("hand-made", null)},{App("web", """{"deployify-managed-by":"deployify","deployify-env":"staging"}""")}]"));

        var diff = await Target(fake).DiffAsync(AcaFixtures.SingleService("myapp"), Prod(), dir.Path, Ct);

        diff.Where(d => d.Kind == DiffKind.Removed).Select(d => d.ServiceName).ShouldBe(["gone"]);
    }

    [Fact]
    public async Task Delete_DeletesTheApp_AndLeavesTheEnvironment()
    {
        var fake = new FakeProcessRunner();

        await Target(fake).DeleteServiceAsync(Prod(), "api", new DeleteOptions(), Ct);

        var args = fake.Single("containerapp", "delete").Args.ToList();
        args[args.IndexOf("--name") + 1].ShouldBe("api");
        args.ShouldContain("--yes");
        (await Target(fake).DescribeKeptAsync(Prod(), new DeleteOptions(), Ct)).ShouldContain(k => k.Contains("test-env"));
    }
}
