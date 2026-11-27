using System.Linq;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>Which version <c>deployify plugin update</c> moves a locked plugin to.</summary>
public sealed class PluginUpdateTests
{
    private static PluginIndexEntry Entry(params (string Version, string? Yanked, string? Protocol)[] versions) => new()
    {
        Versions = [.. versions.Select(v => new PluginIndexVersion { Version = v.Version, Yanked = v.Yanked, Protocol = v.Protocol })]
    };

    private static PluginIndexEntry Entry(params string[] versions)
        => Entry([.. versions.Select(v => (v, (string?)null, (string?)null))]);

    [Fact]
    public void PicksTheNewestWithoutBreakingChanges()
        => Entry("1.0.0", "1.2.0", "1.10.1", "2.0.0").UpdateFor("1.0.0")?.Version.ShouldBe("1.10.1");

    [Fact]
    public void CrossesAMajorVersionOnlyWhenAsked()
        => Entry("1.0.0", "1.2.0", "2.0.0").UpdateFor("1.0.0", crossMajor: true)?.Version.ShouldBe("2.0.0");

    [Fact]
    public void NothingNewer_IsNull()
        => Entry("1.0.0", "1.2.0").UpdateFor("1.2.0").ShouldBeNull();

    [Fact]
    public void SkipsYankedVersions()
        => Entry(("1.0.0", null, null), ("1.1.0", null, null), ("1.2.0", "broke the rollout", null)).UpdateFor("1.0.0")?.Version.ShouldBe("1.1.0");

    [Fact]
    public void SkipsVersionsThatSpeakAnotherProtocol()
        => Entry(("1.0.0", null, "1.0"), ("1.1.0", null, "1.0"), ("1.2.0", null, "2.0")).UpdateFor("1.0.0")?.Version.ShouldBe("1.1.0");

    [Fact]
    public void PreReleases_OnlyWhenAskedOrAlreadyOnOne()
    {
        var entry = Entry("1.0.0", "1.1.0-beta.1");
        entry.UpdateFor("1.0.0").ShouldBeNull();
        entry.UpdateFor("1.0.0", includePrerelease: true)?.Version.ShouldBe("1.1.0-beta.1");
        entry.UpdateFor("1.1.0-alpha.3")?.Version.ShouldBe("1.1.0-beta.1");
    }

    [Fact]
    public void Latest_OrdersPreReleaseNumbersAsNumbers()
        => Entry("1.0.0-beta.2", "1.0.0-beta.10").Latest(includePrerelease: true)?.Version.ShouldBe("1.0.0-beta.10");
}
