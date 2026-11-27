using System.Linq;
using Deployify.Core.Versioning;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Versioning;

public sealed class VersionOrderTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.9", "1.0.0-alpha.10")]
    [InlineData("1.0.0-alpha.16.60", "1.0.0-beta")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("v1.2.0", "1.3.0")]
    public void OrdersBySemVerPrecedence(string lower, string higher)
    {
        VersionOrder.Compare(lower, higher).ShouldBeLessThan(0);
        VersionOrder.Compare(higher, lower).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void IgnoresBuildMetadata()
        => VersionOrder.Compare("1.2.0+185a337", "1.2.0").ShouldBe(0);

    [Fact]
    public void Sorts()
        => new[] { "1.10.0", "1.2.0", "1.2.0-beta.2", "1.2.0-beta.10", "0.9.0" }
            .Order(VersionOrder.Comparer)
            .ShouldBe(["0.9.0", "1.2.0-beta.2", "1.2.0-beta.10", "1.2.0", "1.10.0"]);

    [Theory]
    [InlineData("1.2.0", "1.9.3", true)]
    [InlineData("1.2.0", "2.0.0", false)]
    [InlineData("0.3.1", "0.3.9", true)]
    [InlineData("0.3.1", "0.4.0", false)]
    public void IsCompatible_WithinTheMajorVersion_AndForZeroXTheMinor(string current, string candidate, bool compatible)
        => VersionOrder.IsCompatible(current, candidate).ShouldBe(compatible);

    [Theory]
    [InlineData("1.2.0", "1.3.0", false, false, true)]
    [InlineData("1.2.0", "1.2.0", false, false, false)]
    [InlineData("1.2.0", "1.1.0", false, false, false)]
    [InlineData("1.2.0", "1.3.0-beta.1", false, false, false)]
    [InlineData("1.2.0", "1.3.0-beta.1", true, false, true)]
    [InlineData("1.3.0-beta.1", "1.3.0-beta.2", false, false, true)]
    [InlineData("1.2.0", "2.0.0", false, false, false)]
    [InlineData("1.2.0", "2.0.0", false, true, true)]
    public void IsUpdate(string current, string candidate, bool prerelease, bool crossMajor, bool expected)
        => VersionOrder.IsUpdate(current, candidate, prerelease, crossMajor).ShouldBe(expected);
}
