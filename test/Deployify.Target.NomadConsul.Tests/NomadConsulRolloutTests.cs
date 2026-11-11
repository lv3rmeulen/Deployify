using System.IO;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.NomadConsul.Tests;

public sealed class NomadConsulRolloutTests
{
    private static NomadConsulDeploymentTarget Target()
        => new(Substitute.For<IPublisher>(), NullLogger<NomadConsulDeploymentTarget>.Instance,
            NullLoggerFactory.Instance);

    [Fact]
    public void Target_SupportsHealthGatedRollouts()
        => Target().ShouldBeAssignableTo<IRolloutTarget>();

    [Fact]
    public async Task GeneratedJobs_HaveAutoRevert_ByDefault()
    {
        using var dir = new Fixtures.TempDir();

        var paths = await Target().GenerateAsync(
            Fixtures.SimpleManifest(), Fixtures.EnvConfig(), dir.Path, TestContext.Current.CancellationToken);

        foreach (var p in paths)
        {
            if (p.EndsWith(".job.hcl"))
            {
                (await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken)).ShouldContain("auto_revert = true");
            }
        }
    }

    [Fact]
    public async Task AutoRevert_CanBeTurnedOff()
    {
        using var dir = new Fixtures.TempDir();
        var cfg = Fixtures.DefaultConsulCfg();
        cfg.AutoRevert = false;
        var env = Fixtures.EnvConfig(consulCfg: cfg);

        var paths = await Target().GenerateAsync(
            Fixtures.SingleService("api"), env, dir.Path, TestContext.Current.CancellationToken);

        foreach (var p in paths)
        {
            if (p.EndsWith(".job.hcl"))
            {
                (await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken)).ShouldNotContain("auto_revert = true");
            }
        }

        (await Target().HasNativeRollbackAsync(env, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
