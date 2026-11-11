using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Deployments;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Targets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>
/// The sample Compose plugin, started as a real child process: what a user's machine does.
/// </summary>
public sealed class ProcessPluginTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string PluginDll = Path.Combine(AppContext.BaseDirectory, "deployify-target-compose-plugin.dll");

    private readonly string _work = Directory.CreateTempSubdirectory("deployify-e2e-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PluginHost Host(string path)
        => new(new PluginLaunchSpec { ExecutablePath = path, Source = "test" }, NullLoggerFactory.Instance,
            new PluginHostOptions { HandshakeTimeout = TimeSpan.FromSeconds(60) });

    private static AspireManifest Manifest() => JsonSerializer.Deserialize<AspireManifest>(
        """
        {
          "resources": {
            "api": {
              "type": "container.v0", "image": "nginx:1.27",
              "env": { "DB_PASSWORD": "{db-password.value}" },
              "bindings": { "http": { "scheme": "http", "protocol": "tcp", "transport": "http", "targetPort": 80 } }
            },
            "cache": { "type": "container.v0", "image": "redis:7" },
            "db-password": {
              "type": "parameter.v0", "value": "{db-password.inputs.value}",
              "inputs": { "value": { "type": "string", "secret": true } }
            }
          }
        }
        """)!;

    private static EnvironmentConfig Config() => new()
    {
        Target = DeploymentTargetType.Parse("compose-plugin"),
        ImageTag = "v1",
        Parameters = { ["db-password"] = "s3cret-from-host" },
        TargetConfig = { ["compose"] = JsonSerializer.SerializeToElement(new { ProjectName = "e2e", GenerateEnvFile = true }) }
    };

    [Fact]
    public async Task ComposePlugin_GeneratesFilesInAChildProcess()
    {
        await using var host = Host(PluginDll);
        var target = new RemoteDeploymentTarget(host, DeploymentTargetType.Parse("compose-plugin"));
        await target.StartAsync(Ct);

        target.DisplayName.ShouldBe("Docker Compose (plugin)");
        ((IDeploymentTarget)target).Supports<IRolloutTarget>().ShouldBeTrue();
        ((IDeploymentTarget)target).Supports<IResourceLifecycle>().ShouldBeTrue();
        target.Info!.ConfigKey.ShouldBe("compose");

        var output = Path.Combine(_work, "out");
        var files = await target.GenerateAsync(Manifest(), Config(), output, Ct);

        files.ShouldNotBeEmpty();
        files.ShouldAllBe(f => File.Exists(f));
        var everything = string.Join("\n", files.Select(File.ReadAllText));
        everything.ShouldContain("nginx:1.27");
        everything.ShouldContain("redis:7");

        // The secret came from Deployify's parameter sources, asked for by the plugin mid-call,
        // and the target kept it out of compose.yml and in its .env file, as the built-in one does.
        everything.ShouldNotContain("s3cret-from-host");
        Directory.EnumerateFiles(output, "*.env", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ShouldContain(text => text.Contains("s3cret-from-host"));

        var explained = await target.ExplainAsync(Manifest(), Config(), Ct);
        explained.Select(e => e.Name).ShouldBe(["api", "cache"], ignoreOrder: true);
    }

    [Fact]
    public async Task MissingExecutable_SaysSo()
    {
        await using var host = Host(Path.Combine(_work, "deployify-target-gone" + (OperatingSystem.IsWindows() ? ".exe" : "")));

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain("Could not start plugin 'deployify-target-gone'");
    }

    [Fact]
    public async Task APluginThatDiesAtStartup_ReportsItsExitCodeAndOutput()
    {
        var script = WriteScript("deployify-target-broken", "echo cannot read config >&2", "exit 3");
        await using var host = Host(script);

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain("exited with code 3");
        ex.Message.ShouldContain("cannot read config");
    }

    [Fact]
    public async Task Session_FindsThePluginInTheProjectFolder_AndStopsItWhenDone()
    {
        // A project that deploys to the plugin target, with the plugin in .deployify/plugins.
        var project = Path.Combine(_work, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "deployify-state.json"), """
            {
              "ActiveEnvironment": "local",
              "Environments": {
                "local": { "Target": "compose-plugin", "TargetConfig": { "compose": { "ProjectName": "e2e" } } }
              }
            }
            """, Ct);
        WriteScript(
            Path.Combine(".deployify", "plugins", "deployify-target-compose-plugin"),
            $"exec dotnet \"{PluginDll}\"", windowsLine: $"@dotnet \"{PluginDll}\"", directory: project);

        await using var services = new ServiceCollection().AddLogging().AddDeployify().BuildServiceProvider();
        RemoteDeploymentTarget remote;
        await using (var session = await DeploymentSessionResolver.ResolveAsync(services, new DeploymentSessionRequest
        {
            ProjectPath = project
        }, Ct))
        {
            remote = session.Target.ShouldBeOfType<RemoteDeploymentTarget>();
            session.TargetType.Value.ShouldBe("compose-plugin");
            remote.Info.ShouldNotBeNull("the resolver starts the plugin");

            var explained = await session.Target.ExplainAsync(Manifest(), session.Config, Ct);
            explained.Count.ShouldBe(2);
        }

        // Disposing the session stopped the process; the next call starts it again.
        (await remote.ExplainAsync(Manifest(), Config(), Ct)).Count.ShouldBe(2);
        await remote.DisposeAsync();
    }

    /// <summary>Writes a tiny plugin "executable": a shell script, or a .cmd file on Windows.</summary>
    private string WriteScript(string relativePath, string unixLine, string? exitLine = null, string? windowsLine = null, string? directory = null)
    {
        var path = Path.Combine(directory ?? _work, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            path += ".cmd";
            var lines = new List<string> { "@echo off" };
            lines.Add(windowsLine ?? unixLine.Replace(">&2", "1>&2"));
            if (exitLine is not null)
            {
                lines.Add(exitLine.Replace("exit", "exit /b"));
            }

            File.WriteAllLines(path, lines);
        }
        else
        {
            File.WriteAllText(path, $"#!/bin/sh\n{unixLine}\n{exitLine}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
