using System.Linq;
using System.Text.Json;
using Deployify.Core.Deployments;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Deployments;

public sealed class ManifestAuditTests
{
    /// <summary>One resource of every type Deployify knows, plus some it doesn't, as Aspire writes them.</summary>
    private const string EveryType = """
    {
      "resources": {
        "api":       { "type": "project.v1", "path": "Api/Api.csproj", "env": { "ConnectionStrings__db": "{appdb.connectionString}" } },
        "web":       { "type": "project.v0", "path": "Web/Web.csproj" },
        "cache":     { "type": "container.v0", "image": "redis:7" },
        "pg":        { "type": "container.v1", "image": "postgres:17", "connectionString": "Host={pg.bindings.tcp.host}" },
        "appdb":     { "type": "value.v0", "connectionString": "{pg.connectionString};Database=app" },
        "legacydb":  { "type": "postgres.database.v0", "parent": "pg", "connectionString": "{pg.connectionString};Database=legacy" },
        "queue":     { "type": "rabbitmq.vhost.v0", "parent": "cache", "connectionString": "{cache.connectionString}" },
        "pw":        { "type": "parameter.v0", "value": "{pw.inputs.value}", "inputs": { "value": { "type": "string", "secret": true } } },
        "text":      { "type": "annotated.string", "value": "x" },
        "sidecar":   { "type": "dapr.v0", "dapr": { "application": "api" } },
        "statestore":{ "type": "dapr.component.v0", "daprComponent": { "type": "state" } },
        "frontend":  { "type": "dockerfile.v0", "path": "frontend/Dockerfile", "context": "frontend" },
        "built":     { "type": "container.v1", "build": { "context": "x", "dockerfile": "x/Dockerfile" } },
        "npm":       { "type": "executable.v0", "command": "npm", "args": ["run", "start"] },
        "storage":   { "type": "azure.bicep.v0", "path": "storage.bicep" },
        "stack":     { "type": "aws.cloudformation.stack.v0" },
        "future":    { "type": "project.v2", "path": "Future/Future.csproj" },
        "mystery":   { "type": "mystery.thing.v0" }
      }
    }
    """;

    private static AspireManifest Parse(string json) => JsonSerializer.Deserialize<AspireManifest>(json)!;

    [Theory]
    [InlineData("api", ResourceKind.Workload)]
    [InlineData("web", ResourceKind.Workload)]
    [InlineData("cache", ResourceKind.Workload)]
    [InlineData("pg", ResourceKind.Workload)]
    [InlineData("appdb", ResourceKind.Input)]
    [InlineData("legacydb", ResourceKind.Input)]
    [InlineData("queue", ResourceKind.Input)]
    [InlineData("pw", ResourceKind.Input)]
    [InlineData("text", ResourceKind.Input)]
    [InlineData("sidecar", ResourceKind.Folded)]
    [InlineData("statestore", ResourceKind.Folded)]
    [InlineData("frontend", ResourceKind.Unsupported)]
    [InlineData("built", ResourceKind.Unsupported)]
    [InlineData("npm", ResourceKind.Unsupported)]
    [InlineData("storage", ResourceKind.Unsupported)]
    [InlineData("stack", ResourceKind.Unsupported)]
    [InlineData("future", ResourceKind.Unknown)]
    [InlineData("mystery", ResourceKind.Unknown)]
    public void EveryResource_IsClassified(string name, ResourceKind expected)
    {
        ResourceTypes.Classify(name, Parse(EveryType)).Kind.ShouldBe(expected);
    }

    [Fact]
    public void OnlyWorkloads_AreDeployable()
    {
        var manifest = Parse(EveryType);

        manifest.Resources.Where(r => r.Value.IsDeployable).Select(r => r.Key).Order()
            .ShouldBe(["api", "cache", "pg", "web"]);
    }

    [Fact]
    public void NewerManifestFormat_AsksForAnUpdate()
    {
        ResourceTypes.Classify("future", Parse(EveryType)).Reason!.ShouldContain("Update Deployify");
    }

    [Fact]
    public void UnusedGaps_AreWarnings_AndListedAsSkipped()
    {
        var result = ManifestAudit.Run(Parse(EveryType));

        result.HasErrors.ShouldBeFalse();
        result.Diagnostics.Where(d => d.Severity == ManifestDiagnosticSeverity.Warning).Select(d => d.Resource)
            .ShouldBe(["built", "frontend", "future", "mystery", "npm", "stack", "storage"]);
        result.Skipped.Select(s => s.Name).ShouldBe(["built", "frontend", "future", "mystery", "npm", "stack", "storage"]);
        result.Blocks(strict: false).ShouldBeFalse();
        result.Blocks(strict: true).ShouldBeTrue();
    }

    [Fact]
    public void GapAWorkloadDependsOn_IsAnError_NamingTheWorkload()
    {
        var manifest = Parse("""
        {
          "resources": {
            "api": { "type": "project.v0", "path": "Api/Api.csproj", "env": { "BACKEND": "{npm.bindings.http.url}" } },
            "npm": { "type": "executable.v0", "command": "npm" }
          }
        }
        """);

        var result = ManifestAudit.Run(manifest);

        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Severity.ShouldBe(ManifestDiagnosticSeverity.Error);
        error.Message.ShouldContain("'api' depends on it");
        result.Blocks(strict: false).ShouldBeTrue();
    }

    [Fact]
    public void DependenciesThroughInputs_AreFollowed()
    {
        // api → value → azure child (unknown type, parent not a workload) → azure resource
        var manifest = Parse("""
        {
          "resources": {
            "api":     { "type": "project.v0", "path": "Api/Api.csproj", "env": { "ConnectionStrings__blobs": "{conn.value}" } },
            "conn":    { "type": "value.v0", "value": "{blobs.connectionString}" },
            "blobs":   { "type": "azure.storage.blob.v0", "parent": "storage", "connectionString": "{storage.outputs.blobEndpoint}" },
            "storage": { "type": "azure.bicep.v0", "path": "storage.bicep" }
          }
        }
        """);

        var result = ManifestAudit.Run(manifest);

        result.Diagnostics.Where(d => d.Severity == ManifestDiagnosticSeverity.Error).Select(d => d.Resource)
            .ShouldBe(["blobs", "storage"]);
    }

    [Fact]
    public void IgnoredGaps_AreInfo_AndDontBlock()
    {
        var manifest = Parse("""
        {
          "resources": {
            "api": { "type": "project.v0", "path": "Api/Api.csproj", "env": { "BACKEND": "{npm.bindings.http.url}" } },
            "npm": { "type": "executable.v0", "command": "npm" }
          }
        }
        """);

        var result = ManifestAudit.Run(manifest, ignoreResources: ["npm"]);

        var info = result.Diagnostics.ShouldHaveSingleItem();
        info.Severity.ShouldBe(ManifestDiagnosticSeverity.Info);
        info.Message.ShouldContain("make sure they get it another way");
        result.Blocks(strict: true).ShouldBeFalse();
        result.Skipped.ShouldHaveSingleItem().Name.ShouldBe("npm");
    }

    [Fact]
    public void StaleIgnoreEntries_AreWarnings()
    {
        var result = ManifestAudit.Run(Parse(EveryType), ignoreResources: ["api", "gone"]);

        result.Diagnostics.Where(d => d.Resource is "api" or "gone")
            .Select(d => (d.Resource, d.Severity))
            .ShouldBe([("api", ManifestDiagnosticSeverity.Warning), ("gone", ManifestDiagnosticSeverity.Warning)]);
    }

    [Fact]
    public void TypesTheTargetHandles_AreNotReported()
    {
        var result = ManifestAudit.Run(Parse(EveryType), handledByTarget: ["executable.v0"]);

        result.Diagnostics.ShouldNotContain(d => d.Resource == "npm");
        result.Skipped.ShouldNotContain(s => s.Name == "npm");
    }

    [Fact]
    public void ManifestWithOnlyKnownTypes_HasNoFindings()
    {
        var manifest = Parse("""
        {
          "resources": {
            "api":   { "type": "project.v0", "path": "Api/Api.csproj", "env": { "PW": "{pw.value}" } },
            "cache": { "type": "container.v0", "image": "redis:7" },
            "pw":    { "type": "parameter.v0", "value": "{pw.inputs.value}" }
          }
        }
        """);

        ManifestAudit.Run(manifest).Diagnostics.ShouldBeEmpty();
    }
}
