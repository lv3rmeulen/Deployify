using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Deployify.Plugin.Sdk;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

public sealed class LockAndConfigTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _project = Directory.CreateTempSubdirectory("deployify-lock-").FullName;

    public void Dispose() => Directory.Delete(_project, recursive: true);

    private static PluginTargetInfo Info(JsonElement? schema) => new()
    {
        Id = "full", DisplayName = "Full", ConfigSchema = schema
    };

    private static JsonElement Schema()
    {
        var options = new TargetOptions().WithConfigSchema<FakeConfig>();
        return options.ConfigSchema!.Value;
    }

    private static EnvironmentConfig ConfigWith(object section) => new()
    {
        TargetConfig = { ["full"] = JsonSerializer.SerializeToElement(section) }
    };

    [Fact]
    public void ConfigCheck_ReportsWrongTypesByPath()
    {
        var problems = PluginConfigCheck.Check(Info(Schema()), ConfigWith(new { Region = 5, Replicas = "two" }));

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.StartsWith("TargetConfig.full.Region:"));
        problems.ShouldContain(p => p.StartsWith("TargetConfig.full.Replicas:"));
    }

    [Fact]
    public void ConfigCheck_AcceptsGoodOrMissingSettings()
    {
        PluginConfigCheck.Check(Info(Schema()), ConfigWith(new { Region = "us", Replicas = 3 })).ShouldBeEmpty();
        PluginConfigCheck.Check(Info(Schema()), new EnvironmentConfig()).ShouldBeEmpty();
        PluginConfigCheck.Check(Info(null), ConfigWith(new { Region = 5 })).ShouldBeEmpty();
    }

    [Fact]
    public async Task BadSettings_StopTheCallBeforeThePluginRuns()
    {
        await using var plugin = new InProcessPlugin(p => p
            .AddTarget<FullTarget>("full", "Full", t => t.WithConfigSchema<FakeConfig>()));
        var target = plugin.Target("full");
        await target.StartAsync(Ct);

        var ex = await Should.ThrowAsync<PluginException>(
            () => target.ExplainAsync(new AspireManifest(), ConfigWith(new { Replicas = "many" }), Ct));

        ex.Message.ShouldContain("TargetConfig.full.Replicas");
    }

    [Fact]
    public async Task LockedVersion_MustMatchTheHandshake()
    {
        await using var plugin = new InProcessPlugin(
            p => p.Named("fakes", "1.2.3").AddTarget<FullTarget>("full", "Full"),
            locked: new PluginLock { Plugin = "fakes", Version = "2.0.0" });

        var ex = await Should.ThrowAsync<PluginException>(() => plugin.Target("full").StartAsync(Ct));

        ex.Message.ShouldContain("is version 1.2.3");
        ex.Message.ShouldContain("locks version 2.0.0");
    }

    [Fact]
    public async Task LockFile_RoundTripsAndIsUsedByLocate()
    {
        var bin = Path.Combine(_project, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "deployify-target-example");
        await File.WriteAllTextAsync(exe, "#!/bin/sh\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            File.Move(exe, exe += ".cmd");
        }

        var lockFile = new PluginLockFile();
        lockFile.Plugins["example"] = new PluginLock { Plugin = "example", Version = "1.0.0", Sha256 = PluginLockFile.HashFile(exe) };
        await lockFile.SaveAsync(_project, Ct);

        var catalog = new PluginCatalog(Path.Combine(_project, "none"), name => name == "PATH" ? bin : null);
        var spec = PluginTargetSource.Locate(catalog, DeploymentTargetType.Parse("EXAMPLE"), _project);

        spec.ShouldNotBeNull();
        spec.Lock.ShouldBe(lockFile.Plugins["example"]);
        PluginTargetSource.Locate(catalog, DeploymentTargetType.Parse("example"), projectDirectory: null)!.Lock.ShouldBeNull();
    }

    [Fact]
    public async Task LockedButNotInstalled_IsAnError()
    {
        var lockFile = new PluginLockFile();
        lockFile.Plugins["sample"] = new PluginLock { Plugin = "sample", Version = "3.1.0" };
        await lockFile.SaveAsync(_project, Ct);
        var catalog = new PluginCatalog(Path.Combine(_project, "none"), _ => null);

        Should.Throw<PluginException>(() => PluginTargetSource.Locate(catalog, DeploymentTargetType.Parse("sample"), _project))
            .Message.ShouldContain("no 'deployify-target-sample' executable is installed");
        PluginTargetSource.Locate(catalog, DeploymentTargetType.Parse("other"), _project).ShouldBeNull();
    }

    [Fact]
    public async Task ChangedFile_IsRefusedBeforeItStarts()
    {
        var exe = Path.Combine(_project, OperatingSystem.IsWindows() ? "deployify-target-example.cmd" : "deployify-target-example");
        await File.WriteAllTextAsync(exe, "echo hi\n", Ct);
        var host = new PluginHost(
            new PluginLaunchSpec
            {
                ExecutablePath = exe,
                Lock = new PluginLock { Plugin = "example", Sha256 = new string('0', 64) }
            },
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var _ = host;

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain("isn't the file deployify-plugins.lock.json locked");
    }
}
