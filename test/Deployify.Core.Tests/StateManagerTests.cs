using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class StateManagerTests
{
    private readonly StateManager _sut = new();

    [Fact]
    public async Task RoundTrip_PreservesAllEnvironments()
    {
        using var dir = new TempDir();
        var state = new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = { ["prod"] = Fixtures.NomadConfig(), ["dev"] = Fixtures.NomadConfig("http://dev:4646") }
        };
        await _sut.SaveFileAsync(state, dir.Path, TestContext.Current.CancellationToken);
        var loaded = await _sut.LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        loaded.ActiveEnvironment.ShouldBe("prod");
        loaded.Environments.Count.ShouldBe(2);
    }

    [Fact]
    public async Task LoadActiveAsync_ReturnsActiveEnv()
    {
        using var dir = new TempDir();
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "staging", Environments = { ["staging"] = Fixtures.NomadConfig() } }, dir.Path, TestContext.Current.CancellationToken);
        var (_, name) = await _sut.LoadActiveAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        name.ShouldBe("staging");
    }

    [Fact]
    public async Task LoadActiveAsync_WithOverride_ReturnsOverriddenEnv()
    {
        using var dir = new TempDir();
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "dev", Environments = { ["dev"] = Fixtures.NomadConfig(), ["prod"] = Fixtures.NomadConfig("http://prod:4646") } }, dir.Path, TestContext.Current.CancellationToken);
        var (_, name) = await _sut.LoadActiveAsync(dir.Path, "prod", TestContext.Current.CancellationToken);
        name.ShouldBe("prod");
    }

    [Fact]
    public async Task LoadActiveAsync_ThrowsKeyNotFound_WhenEnvMissing()
    {
        using var dir = new TempDir();
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "dev", Environments = { ["dev"] = Fixtures.NomadConfig() } }, dir.Path, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<KeyNotFoundException>(() => _sut.LoadActiveAsync(dir.Path, "ghost", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadFileAsync_ThrowsFileNotFound_WhenNoStateFile()
    {
        using var dir = new TempDir();
        await Should.ThrowAsync<FileNotFoundException>(() => _sut.LoadFileAsync(dir.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Exists_ReturnsFalse_WhenNoFile()
    {
        using var dir = new TempDir();
        _sut.Exists(dir.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task Exists_ReturnsTrue_AfterSave()
    {
        using var dir = new TempDir();
        await _sut.SaveFileAsync(new DeployifyStateFile { Environments = { ["x"] = Fixtures.NomadConfig() } }, dir.Path, TestContext.Current.CancellationToken);
        _sut.Exists(dir.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task Save_Overwrites_ExistingFile()
    {
        using var dir = new TempDir();
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "v1", Environments = { ["v1"] = Fixtures.NomadConfig() } }, dir.Path, TestContext.Current.CancellationToken);
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "v2", Environments = { ["v2"] = Fixtures.NomadConfig() } }, dir.Path, TestContext.Current.CancellationToken);
        var loaded = await _sut.LoadFileAsync(dir.Path, TestContext.Current.CancellationToken);
        loaded.ActiveEnvironment.ShouldBe("v2");
        loaded.Environments.Keys.ShouldContain("v2");
        // single item implied;
    }

    [Fact]
    public async Task Save_PreservesTargetConfigForKubernetes()
    {
        using var dir = new TempDir();
        var config = Fixtures.KubernetesConfig(prefix: "myapp", domain: "example.com");
        await _sut.SaveFileAsync(new DeployifyStateFile { ActiveEnvironment = "prod", Environments = { ["prod"] = config } }, dir.Path, TestContext.Current.CancellationToken);
        var (loaded, _) = await _sut.LoadActiveAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        loaded.Target.ShouldBe(DeploymentTargetType.Kubernetes);
        loaded.TargetConfig.ShouldContainKey("kubernetes");
    }
}
