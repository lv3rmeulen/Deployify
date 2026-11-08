using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.AzureContainerApps.Tests;

/// <summary>The exact az command lines, checked through a fake process runner.</summary>
public sealed class AcaCliArgumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Subscription_IsPrefixedOnlyWhenSet()
    {
        var fake = new FakeProcessRunner();
        new AcaCliClient(new AcaTargetConfig { SubscriptionId = "sub-1" }, NullLogger.Instance, fake)
            .BuildArgs("containerapp", "list").ShouldBe(["--subscription", "sub-1", "containerapp", "list"]);
        new AcaCliClient(new AcaTargetConfig { SubscriptionId = " " }, NullLogger.Instance, fake)
            .BuildArgs("containerapp", "list").ShouldBe(["containerapp", "list"]);
    }

    [Fact]
    public async Task Deploy_SpecPathWithSpaces_IsOneArgument()
    {
        var fake = new FakeProcessRunner()
            .When(["containerapp", "list"], new ProcessResult(0, "[]", ""))
            .When(["containerapp", "create"], new ProcessResult(0, "{\"id\":\"/subs/x/apps/api\"}", ""));
        var client = new AcaCliClient(new AcaTargetConfig { ResourceGroup = "my rg", EnvironmentName = "env" },
            NullLogger.Instance, fake);

        var id = await client.DeployAppAsync(@"C:\My Projects\out\api.yaml", "api", Ct);

        id.ShouldBe("/subs/x/apps/api");
        var args = fake.Single("containerapp", "create").Args;
        fake.Calls.ShouldAllBe(c => c.FileName == "az");
        args[args.ToList().IndexOf("--yaml") + 1].ShouldBe(@"C:\My Projects\out\api.yaml");
        args[args.ToList().IndexOf("--resource-group") + 1].ShouldBe("my rg");
    }
}
