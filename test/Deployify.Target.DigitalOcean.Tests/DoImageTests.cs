using Shouldly;
using Xunit;

namespace Deployify.Target.DigitalOcean.Tests;

public sealed class DoImageTests
{
    [Theory]
    [InlineData("registry.digitalocean.com/acme/api:v1", "DOCR", null, "api", "v1")]
    [InlineData("postgres:17", "DOCKER_HUB", "library", "postgres", "17")]
    [InlineData("docker.io/library/redis:8.2", "DOCKER_HUB", "library", "redis", "8.2")]
    [InlineData("bitnami/kafka:3.9", "DOCKER_HUB", "bitnami", "kafka", "3.9")]
    [InlineData("ghcr.io/acme/catalog:2.1", "GHCR", "acme", "catalog", "2.1")]
    [InlineData("ghcr.io/acme/team/catalog", "GHCR", "acme/team", "catalog", "fallback")]
    public void MapsSupportedRegistries(string image, string type, string? registry, string repository, string tag)
    {
        var source = DoImage.ToSource(image, "fallback", out var error);

        error.ShouldBeNull();
        (source!.RegistryType, source.Registry, source.Repository, source.Tag, source.Digest)
            .ShouldBe((type, registry, repository, (string?)tag, (string?)null));
    }

    [Fact]
    public void ADigest_WinsOverTheTag()
    {
        var digest = "sha256:" + new string('b', 64);

        var source = DoImage.ToSource($"registry.digitalocean.com/acme/api:v1@{digest}", "v1", out _);

        (source!.Tag, source.Digest).ShouldBe(((string?)null, (string?)digest));
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/aspire-dashboard:9.4")]
    [InlineData("localhost:5000/api:dev")]
    [InlineData("registry.digitalocean.com/acme")]
    public void RejectsWhatAppPlatformCantPull(string image)
    {
        DoImage.ToSource(image, null, out var error).ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("postgres:17.6-alpine", "17")]
    [InlineData("docker.io/library/postgres:16", "16")]
    [InlineData("postgres:latest", null)]
    [InlineData("postgres", null)]
    public void MajorVersion(string image, string? expected) => DoImage.MajorVersion(image).ShouldBe(expected);

    [Theory]
    [InlineData("api", "api")]
    [InlineData("Shop.Api", "shop-api")]
    [InlineData("my_worker__1", "my-worker-1")]
    [InlineData("1st", "c-1st")]
    [InlineData("x", "x0")]
    [InlineData("a-very-long-resource-name-that-goes-past-the-limit", "a-very-long-resource-name-that-g")]
    public void ComponentNames(string name, string expected)
    {
        var component = Names.Component(name);

        component.ShouldBe(expected);
        Names.IsValid(component).ShouldBeTrue();
    }
}
