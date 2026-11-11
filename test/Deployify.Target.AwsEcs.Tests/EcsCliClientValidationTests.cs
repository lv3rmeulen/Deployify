using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.AwsEcs.Tests;

/// <summary>
/// Tests for EcsCliClient that only exercise local logic (JSON validation).
/// Network-dependent methods (Ping, Deploy) are not tested here because they
/// require a real AWS CLI and account — they are tested at the integration level
/// via exit-code assertions.
/// </summary>
public sealed class EcsCliClientValidationTests
{
    private readonly EcsTargetConfig _cfg = new() { Region = "us-east-1", ClusterName = "test" };

    private EcsCliClient Client() => new(_cfg, NullLogger.Instance);

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsNoErrors_ForValidJson()
    {
        using var dir = new EcsFixtures.TempDir();
        var generator = new EcsTaskDefinitionGenerator();
        var config = EcsFixtures.EcsConfig();
        var ecsCfg = new EcsTargetConfig();
        var services = await generator.GenerateAsync(EcsFixtures.SingleService("api"), config, ecsCfg, TestContext.Current.CancellationToken);
        var path = await generator.WriteTaskDefinitionAsync(services[0], config, ecsCfg, dir.Path, TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsError_WhenFamilyMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, """{"containerDefinitions": [{"name":"x"}], "networkMode": "awsvpc"}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("family"));
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsError_WhenContainerDefinitionsMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, """{"family": "myapp", "networkMode": "awsvpc"}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("containerDefinitions"));
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsError_WhenContainerDefinitionsEmpty()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, """{"family": "myapp", "containerDefinitions": [], "networkMode": "awsvpc"}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("at least one"));
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsError_WhenNetworkModeMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, """{"family": "myapp", "containerDefinitions": [{"name":"x"}]}""", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("networkMode"));
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsError_WhenInvalidJson()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, "{ not valid json }}}", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.ShouldContain(e => e.Contains("Invalid JSON") || e.Contains("JSON"));
    }

    [Fact]
    public async Task ValidateTaskDefinition_ReturnsMultipleErrors_WhenMultipleFieldsMissing()
    {
        using var dir = new EcsFixtures.TempDir();
        var path = Path.Combine(dir.Path, "bad.taskdef.json");
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);

        var errors = await Client().ValidateTaskDefinitionAsync(path, TestContext.Current.CancellationToken);

        errors.Count.ShouldBeGreaterThan(1);
    }
}
