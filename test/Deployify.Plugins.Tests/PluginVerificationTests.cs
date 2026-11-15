using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>
/// Hash locks are mandatory: a plugin the lock file has no hash for doesn't start unless unverified
/// plugins are allowed, and one whose files changed never does.
/// </summary>
public sealed class PluginVerificationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("deployify-verify-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A plugin that leaves a marker file when it runs, then exits.</summary>
    private string MarkingPlugin(string name = "deployify-target-example")
    {
        var marker = Path.Combine(_root, "ran");
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_root, name + ".cmd");
            File.WriteAllText(path, $"@echo off\r\necho ran> \"{marker}\"\r\nexit /b 3\r\n");
        }
        else
        {
            path = Path.Combine(_root, name);
            File.WriteAllText(path, $"#!/bin/sh\necho ran > \"{marker}\"\nexit 3\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private bool Ran => File.Exists(Path.Combine(_root, "ran"));

    private static PluginHost Host(PluginLaunchSpec spec, ILoggerFactory? loggers = null)
        => new(spec, loggers ?? NullLoggerFactory.Instance, new PluginHostOptions { HandshakeTimeout = TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task UnlockedPlugin_IsRefusedByDefault_BeforeItRuns()
    {
        var exe = MarkingPlugin();
        await using var host = Host(new PluginLaunchSpec { ExecutablePath = exe, Source = "PATH" });

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain("isn't verified: deployify-plugins.lock.json doesn't lock it");
        ex.Message.ShouldContain("deployify plugin lock");
        ex.Message.ShouldContain("--allow-unverified");
        Ran.ShouldBeFalse();
    }

    [Fact]
    public async Task VersionOnlyLock_IsRefused_LikeNoLock()
    {
        var exe = MarkingPlugin();
        await using var host = Host(new PluginLaunchSpec
        {
            ExecutablePath = exe,
            Lock = new PluginLock { Plugin = "example", Version = "1.0.0", Sha256 = new() { ["some-other-rid"] = new string('a', 64) } }
        });

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain($"has no hash for it on {PluginFiles.CurrentPlatform}");
        Ran.ShouldBeFalse();
    }

    [Fact]
    public async Task AllowUnverified_StartsIt_AndWarnsEveryTime()
    {
        var exe = MarkingPlugin();
        var logs = new CapturingLoggers();
        await using var host = Host(new PluginLaunchSpec { ExecutablePath = exe, Unverified = UnverifiedPluginPolicy.Warn }, logs);

        // The fake plugin exits straight away, so each call starts it again.
        (await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct))).Message.ShouldContain("exited with code 3");
        await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        Ran.ShouldBeTrue();
        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("unverified")).ToList();
        warnings.Count.ShouldBe(2);
        warnings[0].Message.ShouldContain("doesn't lock it");
    }

    [Theory]
    [InlineData(UnverifiedPluginPolicy.Refuse)]
    [InlineData(UnverifiedPluginPolicy.Warn)]
    [InlineData(UnverifiedPluginPolicy.Allow)]
    public async Task ChangedFiles_AreRefused_WhateverThePolicy(UnverifiedPluginPolicy policy)
    {
        var exe = MarkingPlugin();
        await using var host = Host(new PluginLaunchSpec
        {
            ExecutablePath = exe,
            Unverified = policy,
            Lock = new PluginLock { Plugin = "example", Sha256 = new() { [PluginFiles.CurrentPlatform] = new string('0', 64) } }
        });

        var ex = await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct));

        ex.Message.ShouldContain("doesn't match deployify-plugins.lock.json");
        Ran.ShouldBeFalse();
    }

    [Fact]
    public async Task MatchingHash_Starts()
    {
        var exe = MarkingPlugin();
        var files = PluginFiles.ForFile(exe);
        await using var host = Host(new PluginLaunchSpec
        {
            ExecutablePath = exe,
            Lock = new PluginLock { Plugin = "example", Sha256 = new() { [files.Platform] = files.ComputeHash() } }
        });

        // It ran (and exited): verification let it through.
        (await Should.ThrowAsync<PluginException>(() => host.GetConnectionAsync(Ct))).Message.ShouldContain("exited with code 3");
        Ran.ShouldBeTrue();
    }

    [Fact]
    public async Task LockFile_WritesHashesByPlatform_AndReadsAnOldSingleHashAsNone()
    {
        var lockFile = new PluginLockFile();
        lockFile.Plugins["example"] = new PluginLock
        {
            Plugin = "example", Version = "1.0.0", Sha256 = new() { ["linux-x64"] = "aa", ["win-x64"] = "bb" }
        };
        await lockFile.SaveAsync(_root, Ct);

        var json = await File.ReadAllTextAsync(PluginLockFile.PathFor(_root), Ct);
        json.ShouldContain("\"linux-x64\": \"aa\"");
        PluginLockFile.Load(_root)!.Find("example")!.HashFor("win-x64").ShouldBe("bb");

        await File.WriteAllTextAsync(PluginLockFile.PathFor(_root),
            """{ "version": 1, "plugins": { "example": { "plugin": "example", "version": "1.0.0", "sha256": "abc123" } } }""", Ct);
        var legacy = PluginLockFile.Load(_root)!.Find("example")!;
        legacy.Sha256.ShouldBeEmpty();
        legacy.HashFor(PluginFiles.AnyPlatform).ShouldBeNull();
    }

    [Fact]
    public void PluginInItsOwnFolder_HashesEveryFileInIt()
    {
        var folder = Path.Combine(_root, "plugins", "example");
        Directory.CreateDirectory(Path.Combine(folder, "runtimes"));
        var entry = Path.Combine(folder, "deployify-target-example.dll");
        File.WriteAllText(entry, "entry");
        File.WriteAllText(Path.Combine(folder, "Dependency.dll"), "v1");
        File.WriteAllText(Path.Combine(folder, "runtimes", "native.so"), "n");

        var files = PluginFiles.ForOwnFolder(entry);
        var before = files.ComputeHash();
        File.WriteAllText(Path.Combine(folder, "Dependency.dll"), "v2");

        files.Folder.ShouldBe(folder);
        files.Platform.ShouldBe(PluginFiles.AnyPlatform);
        files.ComputeHash().ShouldNotBe(before);
        PluginFiles.ForFile(entry).ComputeHash().ShouldBe(PluginLockFile.HashFile(entry));
    }

    [Fact]
    public void Catalog_HashesASubfolderPluginsFolder_ButOnlyTheFileAtTheTop()
    {
        var plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "example"));
        File.WriteAllText(Path.Combine(plugins, "example", "deployify-target-example.dll"), "x");
        File.WriteAllText(Path.Combine(plugins, "deployify-target-sample.dll"), "y");

        var found = new PluginCatalog(plugins, _ => null).Discover();

        found.Single(p => p.TargetId == "example").Files.Folder.ShouldBe(Path.Combine(plugins, "example"));
        found.Single(p => p.TargetId == "sample").Files.Folder.ShouldBeNull();
    }

    [Fact]
    public void DotnetToolShim_IsHashedByTheToolBehindIt()
    {
        var tools = Path.Combine(_root, "tools");
        var any = WriteTool(tools, "1.0.0");
        var shim = WriteShim(tools, any, extra: "machine A");

        var files = PluginFiles.ForSharedFolder(shim);
        var hash = files.ComputeHash();

        files.Folder.ShouldBe(any);
        files.EntryPath.ShouldBe(Path.Combine(any, "Example.Plugin.dll"));
        files.Platform.ShouldBe(PluginFiles.AnyPlatform);

        // Another machine's launcher for the same tool is different bytes; the hash isn't.
        WriteShim(tools, any, extra: "machine B, another SDK");
        PluginFiles.ForSharedFolder(shim).ComputeHash().ShouldBe(hash);

        // A changed dependency of the tool is noticed.
        File.WriteAllText(Path.Combine(any, "Dependency.dll"), "tampered");
        PluginFiles.ForSharedFolder(shim).ComputeHash().ShouldNotBe(hash);
    }

    [Fact]
    public void DotnetToolShim_WithSeveralVersionsInTheStore_UsesTheOneTheLauncherPointsAt()
    {
        var tools = Path.Combine(_root, "tools");
        WriteTool(tools, "1.0.0");
        var current = WriteTool(tools, "2.0.0");
        var shim = WriteShim(tools, current, extra: "");

        PluginFiles.ForSharedFolder(shim).Folder.ShouldBe(current);
    }

    [Fact]
    public void ExecutableWithoutAToolStore_IsJustTheFile()
    {
        var exe = MarkingPlugin();

        var files = PluginFiles.ForSharedFolder(exe);

        files.Folder.ShouldBeNull();
        files.EntryPath.ShouldBe(exe);
        files.Platform.ShouldBe(PluginFiles.CurrentPlatform);
    }

    /// <summary>What <c>dotnet tool install --tool-path</c> lays out in the store.</summary>
    private static string WriteTool(string tools, string version)
    {
        var any = Path.Combine(tools, ".store", "example.plugin", version, "example.plugin", version, "tools", "net10.0", "any");
        Directory.CreateDirectory(any);
        File.WriteAllText(Path.Combine(any, "Example.Plugin.dll"), "entry " + version);
        File.WriteAllText(Path.Combine(any, "Dependency.dll"), "dependency");
        File.WriteAllText(Path.Combine(any, "DotnetToolSettings.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="deployify-target-example" EntryPoint="Example.Plugin.dll" Runner="dotnet" />
              </Commands>
            </DotNetCliTool>
            """);
        return any;
    }

    /// <summary>A stand-in for the apphost launcher: bytes with the relative entry point path built in.</summary>
    private static string WriteShim(string tools, string toolFolder, string extra)
    {
        var shim = Path.Combine(tools, "deployify-target-example" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var relative = Path.GetRelativePath(tools, Path.Combine(toolFolder, "Example.Plugin.dll"));
        File.WriteAllBytes(shim, Encoding.UTF8.GetBytes($"MZ apphost {extra}\0{relative}\0"));
        return shim;
    }

    private sealed class CapturingLoggers : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
