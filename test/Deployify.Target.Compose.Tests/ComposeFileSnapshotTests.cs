using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Tests.Shared;
using Xunit;

namespace Deployify.Target.Compose.Tests;

/// <summary>
/// Every file the Compose generator writes, for scenarios that use all of its features, compared
/// with checked-in snapshots: YAML by meaning (<see cref="YamlSemantics"/>), .env files as text.
/// </summary>
public sealed class ComposeFileSnapshotTests
{
    private const string ManifestJson = """
        {
          "resources": {
            "pg-password": {
              "type": "parameter.v0",
              "value": "{pg-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            },
            "pg": {
              "type": "container.v0",
              "image": "postgres:17",
              "connectionString": "Host={pg.bindings.tcp.host};Password={pg-password.value}",
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            },
            "api": {
              "type": "project.v0",
              "path": "Api/Api.csproj",
              "env": {
                "ConnectionStrings__db": "{pg.connectionString}",
                "FLAG": "yes",
                "PORT_TEXT": "8080",
                "EMPTY": "",
                "PRICE": "$5 and 'quotes' \"too\""
              },
              "bindings": {
                "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 8080 },
                "metrics": { "scheme": "udp", "protocol": "udp", "transport": "udp", "targetPort": 9125 }
              },
              "references": [ { "name": "pg" } ],
              "annotations": { "dapr/enabled": "true", "dapr/app-id": "api-app" }
            },
            "worker": {
              "type": "container.v0",
              "image": "registry.example.com/myapp/worker:1",
              "references": [ { "name": "api" } ]
            }
          }
        }
        """;

    public static TheoryData<string> Scenarios => new("env-files", "inline-env", "no-profiles-restart-no");

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Files_MatchSnapshot(string scenario)
    {
        var manifest = JsonSerializer.Deserialize<AspireManifest>(ManifestJson)!;
        var config = Fixtures.ComposeConfig();
        config.Parameters["pg-password"] = "S3cret-pw";
        var compose = new ComposeTargetConfig
        {
            ProjectName = "shop",
            GenerateEnvFile = scenario == "env-files",
            GenerateProfiles = scenario != "no-profiles-restart-no",
            RestartPolicy = scenario == "no-profiles-restart-no" ? "no" : "unless-stopped",
            Volumes = new() { ["data"] = null, ["cache"] = "local" },
            BindMounts = new() { ["api"] = ["./config:/app/config:ro"] }
        };

        var generator = new ComposeFileGenerator();
        var services = await generator.GenerateAsync(manifest, config, compose, TestContext.Current.CancellationToken);
        using var dir = new TempDir();
        foreach (var svc in services)
        {
            await generator.WriteServiceFileAsync(svc, dir.Path, compose, TestContext.Current.CancellationToken);
        }

        await generator.WriteMergedFileAsync(services, dir.Path, compose, TestContext.Current.CancellationToken);

        // .gitignore excludes *.env, so .env files are kept under another name.
        var files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/').Replace(".env", "dotenv.snapshot", StringComparison.Ordinal),
                File.ReadAllText, StringComparer.Ordinal);
        Snapshot.Verify($"compose/{scenario}", files,
            (name, content) => name.EndsWith(".yml", StringComparison.Ordinal) ? YamlSemantics.Normalize(content) : content);
    }
}
