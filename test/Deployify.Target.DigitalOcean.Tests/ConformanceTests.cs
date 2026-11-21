using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Plugin.Testing;
using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

/// <summary>Runs the SDK's conformance checks against the plugin, in process and without a DigitalOcean account.</summary>
public sealed class ConformanceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _output = Directory.CreateTempSubdirectory("deployify-do-conformance-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public async Task ThePlugin_KeepsTheContracts()
    {
        var config = DoFixtures.Config();
        // Point the token variable somewhere unset, so the run stays offline whatever the machine has.
        config.TargetConfig[DoTargetConfig.Key] = System.Text.Json.JsonSerializer.SerializeToElement(
            new DoTargetConfig { Region = "ams", TokenVariable = "DEPLOYIFY_TEST_NO_SUCH_TOKEN" });

        var report = await PluginConformance.RunInProcessAsync(
            p => p.Named("digitalocean", "1.0.0").AddTarget<DoDeploymentTarget>(DoDeploymentTarget.Id, "DigitalOcean App Platform",
                t =>
                {
                    t.ConfigKey = DoTargetConfig.Key;
                    t.WithConfigSchema<DoTargetConfig>();
                }),
            DoDeploymentTarget.Id,
            new ConformanceOptions { Manifest = DoFixtures.ShopManifest(), Config = config, OutputDirectory = _output },
            Ct);

        report.Passed.ShouldBeTrue(report.ToString());
    }
}
