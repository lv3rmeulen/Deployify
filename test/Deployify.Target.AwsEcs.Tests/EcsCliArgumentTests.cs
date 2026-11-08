using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>The exact aws command lines, checked through a fake process runner.</summary>
public sealed class EcsCliArgumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EcsTargetConfig Cfg = new()
    {
        Region = "eu-west-1", ClusterName = "my cluster", AwsProfile = "dev profile",
        SubnetIds = ["subnet-a", "subnet-b"], SecurityGroupIds = ["sg-1"]
    };

    [Fact]
    public void ProfileAndRegion_ComeFirst_AsSeparateArguments()
        => new EcsCliClient(Cfg, NullLogger.Instance, new FakeProcessRunner())
            .BuildArgs("ecs", "list-services")
            .ShouldBe(["--profile", "dev profile", "--region", "eu-west-1", "ecs", "list-services"]);

    [Fact]
    public async Task CreateService_PassesEachValueAsOneArgument()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], new ProcessResult(0, "{\"serviceArns\":[]}", ""))
            .When(["create-service"], new ProcessResult(0, "{\"service\":{\"serviceArn\":\"arn:x\"}}", ""));

        await new EcsCliClient(Cfg, NullLogger.Instance, fake).UpsertServiceAsync("api", "arn:td:1", 2, Ct);

        var args = fake.Single("create-service").Args;
        args.ShouldContain("my cluster");
        var networkIndex = args.ToList().IndexOf("--network-configuration");
        args[networkIndex + 1].ShouldBe(
            "awsvpcConfiguration={subnets=[subnet-a,subnet-b],securityGroups=[sg-1],assignPublicIp=ENABLED}");
        args[args.ToList().IndexOf("--desired-count") + 1].ShouldBe("2");
    }

    [Fact]
    public async Task ReadingSecretParameters_IsNotLogged()
    {
        var fake = new FakeProcessRunner
        {
            Default = new ProcessResult(0, "{\"Parameter\":{\"Name\":\"/app/db\",\"Value\":\"s3cret\",\"Version\":3}}", "")
        };

        var p = await new EcsCliClient(Cfg, NullLogger.Instance, fake).GetParameterAsync("/app/db path", Ct);

        p!.Version.ShouldBe(3);
        var call = fake.Calls.Single();
        call.Sensitive.ShouldBeTrue();
        call.Args.ShouldContain("/app/db path");
    }

    [Fact]
    public async Task IsAvailable_FalseWhenAwsMissing()
        => (await EcsCliClient.IsAvailableAsync(new FakeProcessRunner { Missing = { "aws" } }, Ct)).ShouldBeFalse();
}
