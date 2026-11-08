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

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>Ownership tags, and what destroy and prune do on ECS.</summary>
public sealed class EcsLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EcsDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<EcsDeploymentTarget>.Instance) { Runner = fake };

    private static ProcessResult Ok(string json) => new(0, json, "");

    private static EnvironmentConfig Prod()
    {
        var config = EcsFixtures.EcsConfig();
        config.Name = "prod";
        return config;
    }

    private static string Svc(string name, string? env = "prod", bool managed = true, string status = "ACTIVE")
    {
        var tags = !managed ? "[]"
            : env is null ? """[{"key":"deployify-managed-by","value":"deployify"}]"""
            : $$"""[{"key":"deployify-managed-by","value":"deployify"},{"key":"deployify-env","value":"{{env}}"}]""";
        return $$"""{"serviceName":"{{name}}","serviceArn":"arn:aws:ecs:us-east-1:1:service/test-cluster/{{name}}","status":"{{status}}","tags":{{tags}}}""";
    }

    private static FakeProcessRunner Cluster(params string[] services)
    {
        var names = services.Select(s => s.Split("\"serviceName\":\"")[1].Split('"')[0]).ToList();
        return new FakeProcessRunner()
            .When(["list-services"], Ok($$"""{"serviceArns":[{{string.Join(",", names.Select(n => $"\"arn:aws:ecs:us-east-1:1:service/test-cluster/{n}\""))}}]}"""))
            .When(["describe-services"], Ok($$"""{"services":[{{string.Join(",", services)}}],"failures":[]}"""));
    }

    // ── Tagging ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewService_IsCreatedWithTheOwnershipAndEnvironmentTags()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":[]}"""))
            .When(["create-service"], Ok("""{"service":{"serviceArn":"arn:x"}}"""));
        var cfg = new EcsTargetConfig { ClusterName = "c", DeployifyEnvironment = "prod" };

        await new EcsCliClient(cfg, NullLogger.Instance, fake).UpsertServiceAsync("api", "arn:td:1", 1, Ct);

        var args = fake.Single("create-service").Args.ToList();
        args[args.IndexOf("--tags") + 1].ShouldBe("key=deployify-managed-by,value=deployify");
        args[args.IndexOf("--tags") + 2].ShouldBe("key=deployify-env,value=prod");
    }

    [Fact]
    public async Task AnExistingService_IsTaggedOnUpdate_SoOldServicesAreAdopted()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:aws:ecs:us-east-1:1:service/c/api"]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:aws:ecs:us-east-1:1:service/c/api"}}"""));
        var cfg = new EcsTargetConfig { ClusterName = "c", DeployifyEnvironment = "prod" };

        await new EcsCliClient(cfg, NullLogger.Instance, fake).UpsertServiceAsync("api", "arn:td:2", 1, Ct);

        var tag = fake.Single("tag-resource").Args.ToList();
        tag[tag.IndexOf("--resource-arn") + 1].ShouldBe("arn:aws:ecs:us-east-1:1:service/c/api");
        tag.ShouldContain("key=deployify-env,value=prod");
    }

    [Fact]
    public async Task AFailedTag_DoesNotFailTheDeploy()
    {
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok("""{"serviceArns":["arn:aws:ecs:us-east-1:1:service/c/api"]}"""))
            .When(["update-service"], Ok("""{"service":{"serviceArn":"arn:svc/api"}}"""))
            .When(["tag-resource"], new ProcessResult(254, "", "AccessDeniedException: not authorized to perform ecs:TagResource"));

        var arn = await new EcsCliClient(new EcsTargetConfig(), NullLogger.Instance, fake).UpsertServiceAsync("api", "arn:td:2", 1, Ct);

        arn.ShouldBe("arn:svc/api");
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListManaged_ReturnsOnlyTaggedActiveServices_WithTheirEnvironment()
    {
        var fake = Cluster(Svc("api"), Svc("legacy", managed: false), Svc("web", env: "staging"), Svc("old", status: "INACTIVE"));

        var managed = await Target(fake).ListManagedAsync(Prod(), Ct);

        managed.Select(m => (m.Name, m.Environment)).ShouldBe([("api", "prod"), ("web", "staging")]);
        managed[0].TargetId.ShouldBe("arn:aws:ecs:us-east-1:1:service/test-cluster/api");
        fake.Single("describe-services").Args.ShouldContain("TAGS");
    }

    [Fact]
    public async Task ListManaged_DescribesServicesTenAtATime()
    {
        var arns = Enumerable.Range(0, 23).Select(i => $"\"arn:aws:ecs:us-east-1:1:service/test-cluster/s{i}\"");
        var fake = new FakeProcessRunner()
            .When(["list-services"], Ok($$"""{"serviceArns":[{{string.Join(",", arns)}}]}"""))
            .When(["describe-services"], Ok("""{"services":[],"failures":[]}"""));

        await Target(fake).ListManagedAsync(Prod(), Ct);

        fake.Calls.Count(c => c.Args.Contains("describe-services")).ShouldBe(3);
    }

    [Fact]
    public async Task ListManaged_Throws_WhenEcsCantBeAsked()
    {
        var fake = new FakeProcessRunner().When(["list-services"], new ProcessResult(255, "", "Unable to locate credentials"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Target(fake).ListManagedAsync(Prod(), Ct));
        ex.Message.ShouldContain("Unable to locate credentials");
    }

    [Fact]
    public async Task Diff_ListsOnlyTaggedServicesOfThisEnvironment_AsRemoved()
    {
        using var dir = new EcsFixtures.TempDir();
        var fake = Cluster(Svc("myapp"), Svc("gone"), Svc("someone-elses", managed: false), Svc("staging-only", env: "staging"));

        var diff = await Target(fake).DiffAsync(EcsFixtures.SingleService("myapp"), Prod(), dir.Path, Ct);

        diff.Where(d => d.Kind == DiffKind.Removed).Select(d => d.ServiceName).ShouldBe(["gone"]);
    }

    // ── Deleting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTheService_ItsTaskDefinitions_AndItsSecrets()
    {
        var fake = new FakeProcessRunner()
            .When(["list-task-definitions"], Ok("""{"taskDefinitionArns":["arn:aws:ecs:us-east-1:1:task-definition/api:6","arn:aws:ecs:us-east-1:1:task-definition/api:7","arn:aws:ecs:us-east-1:1:task-definition/api-gateway:2"]}"""))
            .When(["get-parameters-by-path"], Ok("""["/deployify/test-cluster/api/DB_PASSWORD"]"""));

        await Target(fake).DeleteServiceAsync(Prod(), "api", new DeleteOptions(), Ct);

        var delete = fake.Single("delete-service").Args;
        delete.ShouldContain("--force");
        fake.Calls.Where(c => c.Args.Contains("deregister-task-definition"))
            .Select(c => c.Args[c.Args.ToList().IndexOf("--task-definition") + 1])
            .ShouldBe(["arn:aws:ecs:us-east-1:1:task-definition/api:6", "arn:aws:ecs:us-east-1:1:task-definition/api:7"]);
        var path = fake.Single("get-parameters-by-path").Args.ToList();
        path[path.IndexOf("--path") + 1].ShouldBe("/deployify/test-cluster/api");
        path.ShouldNotContain("--with-decryption");
        fake.Single("delete-parameters").Args.ShouldContain("/deployify/test-cluster/api/DB_PASSWORD");
    }

    [Fact]
    public async Task Delete_WithKeepSecrets_LeavesTheSsmParameters()
    {
        var fake = new FakeProcessRunner().When(["list-task-definitions"], Ok("""{"taskDefinitionArns":[]}"""));

        await Target(fake).DeleteServiceAsync(Prod(), "api", new DeleteOptions { KeepSecrets = true }, Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("get-parameters-by-path") || c.Args.Contains("delete-parameters"));
    }

    [Fact]
    public async Task Delete_OfAServiceThatIsAlreadyGone_IsFine()
    {
        var fake = new FakeProcessRunner()
            .When(["delete-service"], new ProcessResult(254, "", "An error occurred (ServiceNotFoundException) when calling the DeleteService operation"))
            .When(["list-task-definitions"], Ok("""{"taskDefinitionArns":[]}"""))
            .When(["get-parameters-by-path"], Ok("[]"));

        await Target(fake).DeleteServiceAsync(Prod(), "api", new DeleteOptions(), Ct);
    }
}
