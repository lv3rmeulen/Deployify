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
}
