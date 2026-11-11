using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Deployify.Target.GitOps.Tests;

internal static class Fixtures
{
    public const string RepoUrl = "https://git.example.com/platform/config.git";
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string Secret = "S3cret-pw";

    /// <summary>A project with an external HTTP endpoint, a worker, and a container pinned to a tag.</summary>
    public static AspireManifest Manifest() => new()
    {
        Resources = new Dictionary<string, AspireResource>
        {
            ["api"] = new()
            {
                Type = "project.v0",
                Path = "Api/Api.csproj",
                Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                Bindings = new Dictionary<string, AspireBinding>
                {
                    ["http"] = new() { ContainerPort = 8080, Protocol = "http", IsExternal = true }
                }
            },
            ["worker"] = new()
            {
                Type = "project.v0",
                Path = "Worker/Worker.csproj",
                References = [new AspireReference { Name = "api" }]
            }
        }
    };

    /// <summary>A container whose env holds a secret parameter.</summary>
    public static AspireManifest ManifestWithSecret() => JsonSerializer.Deserialize<AspireManifest>("""
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
              "env": { "POSTGRES_PASSWORD": "{pg-password.value}", "PLAIN": "hello" },
              "bindings": { "tcp": { "scheme": "tcp", "protocol": "tcp", "transport": "tcp", "targetPort": 5432 } }
            }
          }
        }
        """)!;

    public static EnvironmentConfig Config(
        DeploymentTargetType target, string checkout, object? gitops = null, object? controller = null,
        SecretBackendType backend = SecretBackendType.Inline)
    {
        var config = new EnvironmentConfig
        {
            Name = "test",
            Target = target,
            ContainerRegistry = "registry.example.com/myapp",
            ImageTag = "v1.0.0",
            OutputPath = "deployify-output"
        };
        config.TargetConfig["kubernetes"] = Element(new { namespacePrefix = "test", context = "prod" });
        config.TargetConfig["gitops"] = Element(gitops ?? new { repoUrl = RepoUrl, checkoutDirectory = checkout });
        if (controller is not null)
        {
            config.TargetConfig[target == DeploymentTargetType.Flux ? "flux" : "argocd"] = Element(controller);
        }

        config.Secrets.Backend = backend;
        config.Parameters["pg-password"] = Secret;
        return config;
    }

    public static JsonElement Element(object value) => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));

    public static ArgoCdDeploymentTarget Argo(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, fake) { PollInterval = TimeSpan.Zero };

    public static FluxDeploymentTarget Flux(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLoggerFactory.Instance, fake) { PollInterval = TimeSpan.Zero };

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "");

    /// <summary>
    /// git and kubectl as a fake: clone creates the clone folder, the branch exists only when
    /// <paramref name="branchExists"/>, there is something to commit only when
    /// <paramref name="changes"/>, and the controller's CRDs are installed.
    /// </summary>
    public static FakeProcessRunner Tools(bool branchExists = true, bool changes = true)
        => new FakeProcessRunner()
            .When(s => s.FileName == "git" && s.Args.Contains("clone"), s =>
            {
                Directory.CreateDirectory(Path.Combine(s.Args[^1], ".git"));
                return Ok();
            })
            .When(s => s.FileName == "git" && s.Args.Contains("ls-remote"),
                _ => Ok(branchExists ? Commit + "\trefs/heads/main\n" : ""))
            .When(s => s.FileName == "git" && s.Args.Contains("--cached") && s.Args.Contains("--quiet"),
                _ => new ProcessResult(changes ? 1 : 0, "", ""))
            .When(s => s.FileName == "git" && s.Args.Contains("rev-parse"), _ => Ok(Commit + "\n"))
            .When(s => s.FileName == "kubectl" && s.Args.Contains("crd"), s => Ok("customresourcedefinition/" + s.Args[s.Args.ToList().IndexOf("crd") + 1]));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployify-tests-" + System.IO.Path.GetRandomFileName());

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Read(params string[] parts) => System.IO.File.ReadAllText(File(parts));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
