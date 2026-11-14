using Deployify.Core.Rollouts;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Rollouts;

public sealed class ImageTagCheckTests
{
    [Theory]
    [InlineData("registry/api", "latest", true)]
    [InlineData("registry/api", null, true)]
    [InlineData("registry/api", "", true)]
    [InlineData("localhost:5000/api", null, true)]
    [InlineData("redis:latest", null, true)]
    [InlineData("registry/api", "1.4.2", false)]
    [InlineData("redis:7.4", "latest", false)]
    [InlineData("registry/api@sha256:abc", null, false)]
    [InlineData(null, null, false)]
    public void IsMutable(string? image, string? tag, bool expected)
        => ImageTagCheck.IsMutable(image, tag).ShouldBe(expected);

    [Theory]
    [InlineData("latest", true)]
    [InlineData("LATEST", true)]
    [InlineData(" latest ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("1.4.2", false)]
    [InlineData("3f2a9c1e5b7d", false)]
    public void IsMutableTag(string? tag, bool expected)
        => ImageTagCheck.IsMutableTag(tag).ShouldBe(expected);
}
