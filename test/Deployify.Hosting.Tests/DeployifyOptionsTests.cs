using System;
using System.Collections.Generic;
using System.IO;
using Deployify.Core.Models;
using Deployify.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Deployify.Hosting.Tests;

public class DeployifyOptionsTests
{
    private static IConfiguration Section(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Deployify");

    [Fact]
    public void Resolve_KeepsCodeSettings_WhenNothingIsConfigured()
    {
        var code = new DeployifyDeployOptions
        {
            Environment = "prod",
            Target = DeploymentTargetType.Helm,
            Publish = false,
            ImageTag = "1.2.3",
            HealthGate = false,
            AutoRollback = false,
            HealthTimeout = TimeSpan.FromMinutes(3),
            AutoApprove = true
        };

        var resolved = DeployifyDeployStep.Resolve(code, Section([]));

        resolved.Environment.ShouldBe("prod");
        resolved.Target.ShouldBe(DeploymentTargetType.Helm);
        resolved.Publish.ShouldBeFalse();
        resolved.ImageTag.ShouldBe("1.2.3");
        resolved.HealthGate.ShouldBeFalse();
        resolved.AutoRollback.ShouldBeFalse();
        resolved.HealthTimeout.ShouldBe(TimeSpan.FromMinutes(3));
        resolved.AutoApprove.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_ConfigurationWinsOverCode()
    {
        var resolved = DeployifyDeployStep.Resolve(new DeployifyDeployOptions { Environment = "prod" }, Section(new()
        {
            ["Deployify:Environment"] = "staging",
            ["Deployify:Target"] = "kubernetes",
            ["Deployify:Publish"] = "false",
            ["Deployify:ImageTag"] = "abc123",
            ["Deployify:HealthTimeoutSeconds"] = "90",
            ["Deployify:AutoApprove"] = "TRUE"
        }));

        resolved.Environment.ShouldBe("staging");
        resolved.Target.ShouldBe(DeploymentTargetType.Kubernetes);
        resolved.Publish.ShouldBeFalse();
        resolved.ImageTag.ShouldBe("abc123");
        resolved.HealthTimeout.ShouldBe(TimeSpan.FromSeconds(90));
        resolved.AutoApprove.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Deployify:AutoApprove", "yes", "true or false")]
    [InlineData("Deployify:Target", "heroku", "not a known target")]
    [InlineData("Deployify:HealthTimeoutSeconds", "-5", "positive number")]
    public void Resolve_RejectsBadValues(string key, string value, string message)
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => DeployifyDeployStep.Resolve(new DeployifyDeployOptions(), Section(new() { [key] = value })));
        ex.Message.ShouldContain(message);
    }

    [Fact]
    public void GetManifestPath_FollowsAspireOutputPathRules()
    {
        var appHost = Path.Combine(Path.GetTempPath(), "apphost");

        DeployifyDeployStep.GetManifestPath(null, appHost)
            .ShouldBe(Path.Combine(appHost, "aspire-output", "aspire-manifest.json"));
        DeployifyDeployStep.GetManifestPath("/out", appHost)
            .ShouldBe(Path.Combine("/out", "aspire-manifest.json"));
        DeployifyDeployStep.GetManifestPath("/out/custom.json", appHost)
            .ShouldBe("/out/custom.json");
    }
}
