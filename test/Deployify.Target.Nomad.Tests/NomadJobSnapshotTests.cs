using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

/// <summary>Every job file of each <see cref="NomadJobScenarios"/> scenario, compared with checked-in snapshots.</summary>
public sealed class NomadJobSnapshotTests
{
    public static TheoryData<string> Scenarios => new(NomadJobScenarios.Names);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task JobFiles_MatchSnapshot(string scenario)
    {
        var generator = new JobGenerator(NullLogger<JobGenerator>.Instance);
        var config = NomadJobScenarios.Config(scenario);
        using var dir = new TempDir();

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in await generator.GenerateAsync(NomadJobScenarios.Manifest(), config, TestContext.Current.CancellationToken))
        {
            var path = await generator.WriteJobAsync(job, config, dir.Path, TestContext.Current.CancellationToken);
            files[Path.GetFileName(path)] = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        }

        Snapshot.Verify($"nomad/{scenario}", files);
    }
    /// <summary>
    /// The JSON job files describe the same jobs as the HCL files earlier versions wrote
    /// (<c>Snapshots/nomad-hcl</c>), according to a real Nomad agent. See <see cref="NomadEquivalence"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task JobFiles_AreTheSameJobs_AsTheHclTheyReplace(string scenario)
    {
        Assert.SkipWhen(NomadEquivalence.Address is null, $"Set {NomadEquivalence.AddressVariable} to a Nomad agent to run this.");

        var snapshots = Path.Combine(SnapshotDirectory(), "Snapshots");
        foreach (var hcl in Directory.GetFiles(Path.Combine(snapshots, "nomad-hcl", scenario), "*.job.hcl"))
        {
            var json = Path.Combine(snapshots, "nomad", scenario, Path.GetFileName(hcl).Replace(".job.hcl", ".nomad.json"));
            var diff = await NomadEquivalence.DiffAsync(NomadEquivalence.Address!, hcl, json, TestContext.Current.CancellationToken);
            Assert.True(diff is null, $"{scenario}/{Path.GetFileName(json)} differs from its HCL:\n{diff}");
        }
    }

    private static string SnapshotDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "")
        => Path.GetDirectoryName(file)!;
}
