using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

/// <summary>Every job file of each <see cref="ConsulJobScenarios"/> scenario, compared with checked-in snapshots.</summary>
public sealed class ConsulJobSnapshotTests
{
    public static TheoryData<string> Scenarios => new(ConsulJobScenarios.Names);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task JobFiles_MatchSnapshot(string scenario)
    {
        var generator = new ConsulJobGenerator(NullLogger<ConsulJobGenerator>.Instance);
        var config = ConsulJobScenarios.Config(scenario);
        var cfg = ConsulJobScenarios.ConsulConfig(scenario);
        using var dir = new Fixtures.TempDir();

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in await generator.GenerateAsync(ConsulJobScenarios.Manifest(), config, cfg, TestContext.Current.CancellationToken))
        {
            var path = await generator.WriteJobAsync(job, config, cfg, dir.Path, TestContext.Current.CancellationToken);
            files[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        }

        Snapshot.Verify($"nomadconsul/{scenario}", files);
    }
    /// <summary>
    /// The JSON job files describe the same jobs as the HCL files earlier versions wrote
    /// (<c>Snapshots/nomadconsul-hcl</c>), according to a real Nomad agent. See <see cref="NomadEquivalence"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task JobFiles_AreTheSameJobs_AsTheHclTheyReplace(string scenario)
    {
        Assert.SkipWhen(NomadEquivalence.Address is null, $"Set {NomadEquivalence.AddressVariable} to a Nomad agent to run this.");

        var snapshots = Path.Combine(SnapshotDirectory(), "Snapshots");
        foreach (var hcl in Directory.GetFiles(Path.Combine(snapshots, "nomadconsul-hcl", scenario), "*.job.hcl"))
        {
            var json = Path.Combine(snapshots, "nomadconsul", scenario, Path.GetFileName(hcl).Replace(".job.hcl", ".nomad.json"));
            var diff = await NomadEquivalence.DiffAsync(NomadEquivalence.Address!, hcl, json, TestContext.Current.CancellationToken);
            Assert.True(diff is null, $"{scenario}/{Path.GetFileName(json)} differs from its HCL:\n{diff}");
        }
    }

    private static string SnapshotDirectory() => Snapshot.SourceDirectory();
}
