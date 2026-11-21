using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Expressions;
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
        "noimage":   { "type": "container.v1", "env": { "X": "1" } },
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
    [InlineData("frontend", ResourceKind.Workload)]
    [InlineData("built", ResourceKind.Workload)]
    [InlineData("noimage", ResourceKind.Unsupported)]
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
            .ShouldBe(["api", "built", "cache", "frontend", "pg", "web"]);
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
            .ShouldBe(["future", "mystery", "noimage", "npm", "stack", "storage"]);
        result.Skipped.Select(s => s.Name).ShouldBe(["future", "mystery", "noimage", "npm", "stack", "storage"]);
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

    /// <summary>Parameters the way Aspire writes them, and who uses them.</summary>
    private const string WithParameters = """
    {
      "resources": {
        "api":      { "type": "project.v0", "path": "Api/Api.csproj",
                      "env": { "ADMIN_KEY": "{admin-key.value}", "ConnectionStrings__db": "{db.connectionString}", "REGION": "{region.value}" } },
        "pg":       { "type": "container.v0", "image": "postgres:17", "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
                      "connectionString": "Host=pg;Password={pg-password.value}" },
        "db":       { "type": "value.v0", "connectionString": "{pg.connectionString};Username={db-user.value}" },
        "admin-key":   { "type": "parameter.v0", "value": "{admin-key.inputs.value}",
                         "inputs": { "value": { "type": "string", "secret": true } } },
        "db-user":     { "type": "parameter.v0", "value": "{db-user.inputs.value}",
                         "inputs": { "value": { "type": "string" } } },
        "region":      { "type": "parameter.v0", "value": "{region.inputs.value}",
                         "inputs": { "value": { "type": "string", "default": { "value": "westeurope" } } } },
        "pg-password": { "type": "parameter.v0", "value": "{pg-password.inputs.value}",
                         "inputs": { "value": { "type": "string", "secret": true, "default": { "generate": { "minLength": 22 } } } } },
        "unused":      { "type": "parameter.v0", "value": "{unused.inputs.value}",
                         "inputs": { "value": { "type": "string", "secret": true } } }
      }
    }
    """;

    /// <summary>The usual lookup order with only <paramref name="values"/> set and no environment variables.</summary>
    private static IParameterSource Source(params (string Name, string Value)[] values)
        => ParameterSources.Default(values.ToDictionary(v => v.Name, v => v.Value), getVariable: _ => null);

    private static Task<ManifestAuditResult> CheckParametersAsync(IParameterSource parameters)
    {
        var manifest = Parse(WithParameters);
        return ManifestAudit.CheckParametersAsync(
            ManifestAudit.Run(manifest), manifest, parameters, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ParameterWithoutValue_ThatAServiceUses_IsAnError()
    {
        var result = await CheckParametersAsync(Source(("db-user", "app")));

        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Resource.ShouldBe("admin-key");
        error.Severity.ShouldBe(ManifestDiagnosticSeverity.Error);
        error.Message.ShouldStartWith("Secret parameter 'admin-key' has no value, and 'api' depends on it.");
        error.Message.ShouldContain("Parameters__admin_key");
        error.Message.ShouldContain("\"admin-key\": \"env:NAME\"");
        result.Blocks(strict: false).ShouldBeTrue();
        result.BlockedSummary(strict: false).ShouldBe(
            "1 problem(s) with resources that deployed services depend on; nothing was changed.");
        result.MissingValues.ShouldBe([error]);
    }

    [Fact]
    public async Task ParameterUsedThroughAConnectionString_IsChecked()
    {
        var result = await CheckParametersAsync(Source(("admin-key", "k")));

        var error = result.Diagnostics.ShouldHaveSingleItem();
        error.Resource.ShouldBe("db-user");
        error.Message.ShouldStartWith("Parameter 'db-user' has no value, and 'api' depends on it.");
    }

    [Fact]
    public async Task EveryValueSupplied_HasNoFindings()
    {
        var result = await CheckParametersAsync(Source(("admin-key", "k"), ("db-user", "app")));

        result.Diagnostics.ShouldBeEmpty();
        result.MissingValues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValueFromAnEnvironmentVariable_Counts()
    {
        var variables = new Dictionary<string, string>
        {
            ["Parameters__admin_key"] = "k",
            ["Parameters__db_user"] = "app"
        };

        var result = await CheckParametersAsync(ParameterSources.Default(getVariable: name => variables.GetValueOrDefault(name)));

        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task GeneratedInputs_AreNotAskedFor_SoNothingIsGenerated()
    {
        var asked = new AskedSource(Source(("admin-key", "k"), ("db-user", "app")));

        await CheckParametersAsync(asked);

        asked.Asked.ShouldNotContain("pg-password");
        asked.Asked.Order().ShouldBe(["admin-key", "db-user", "region"]);
    }

    [Fact]
    public async Task ParameterNoDeployedServiceUses_IsNotChecked()
    {
        var result = await CheckParametersAsync(Source(("admin-key", "k"), ("db-user", "app")));

        result.Diagnostics.ShouldNotContain(d => d.Resource == "unused");
    }

    /// <summary>Records which parameters were asked for.</summary>
    private sealed class AskedSource(IParameterSource inner) : IParameterSource
    {
        public List<string> Asked { get; } = [];

        public ValueTask<string?> GetValueAsync(string parameter, ParameterInput input, CancellationToken ct = default)
        {
            Asked.Add(parameter);
            return inner.GetValueAsync(parameter, input, ct);
        }
    }
}
