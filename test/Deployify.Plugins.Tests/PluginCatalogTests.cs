using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deployify-plugins-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Touch(string directory, string name, bool executable = true)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows() && executable)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Creates a fake plugin executable named the way the current OS needs: with <c>.exe</c> on
    /// Windows, which only accepts executable extensions, and with no extension elsewhere.
    /// </summary>
    private string Executable(string directory, string name)
        => Touch(directory, _windows ? name + ".exe" : name);

    // The catalog's Windows rules follow this, so every test runs the rules of the OS it runs on.
    private readonly bool _windows = OperatingSystem.IsWindows();

    /// <summary>
    /// A catalog that sees only <paramref name="variables"/> as its environment: PATH and
    /// DEPLOYIFY_PLUGIN_PATH point at temporary folders, never at the machine's real ones.
    /// </summary>
    private PluginCatalog Catalog(Dictionary<string, string?> variables, string? user = null, bool? windows = null)
        => new(user ?? Path.Combine(_root, "no-user-dir"), name => variables.GetValueOrDefault(name), windows ?? _windows);

    [Fact]
    public void FindsAPluginOnPath()
    {
        var bin = Dir("bin");
        var exe = Executable(bin, "deployify-target-example");

        var found = Catalog(new() { ["PATH"] = bin }).Find(DeploymentTargetType.Parse("EXAMPLE"));

        found.ShouldNotBeNull();
        found.ExecutablePath.ShouldBe(exe);
        found.Source.ShouldBe("PATH");
    }

    [Fact]
    public void PluginPathVariable_WinsOverPath()
    {
        var first = Dir("first");
        var bin = Dir("bin");
        var preferred = Executable(first, "deployify-target-example");
        Executable(bin, "deployify-target-example");

        Catalog(new() { ["PATH"] = bin, [PluginCatalog.PluginPathVariable] = first })
            .Find(DeploymentTargetType.Parse("example"))!.ExecutablePath.ShouldBe(preferred);
    }

    [Fact]
    public void ProjectFolder_IsSearchedWhenTheProjectIsKnown()
    {
        var project = Dir("project");
        var exe = Executable(PluginCatalog.ProjectDirectoryFor(project), Path.Combine("sample", "deployify-target-sample"));
        var catalog = Catalog([]);

        catalog.Find(DeploymentTargetType.Parse("sample")).ShouldBeNull();
        catalog.Find(DeploymentTargetType.Parse("sample"), project)!.ExecutablePath.ShouldBe(exe);
    }

    [Fact]
    public void UserFolder_AllowsAFrameworkDependentDll()
    {
        var user = Dir("user");
        var dll = Touch(user, Path.Combine("example", "deployify-target-example.dll"), executable: false);

        Catalog([], user).Find(DeploymentTargetType.Parse("example"))!.ExecutablePath.ShouldBe(dll);
    }

    [Fact]
    public void Path_IgnoresDllsAndFilesThatAreNotExecutables()
    {
        var bin = Dir("bin");
        Touch(bin, "deployify-target-example.dll", executable: false);
        Touch(bin, "deployify-target-example.pdb", executable: false);
        Touch(bin, "deployify-target-example.json", executable: false);

        Catalog(new() { ["PATH"] = bin }, windows: false).Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Windows_NeedsAnExecutableExtension()
    {
        var catalog = Catalog([], windows: true);

        catalog.TargetIdFor("deployify-target-example.exe", allowDll: false).ShouldBe("example");
        catalog.TargetIdFor("deployify-target-example.cmd", allowDll: false).ShouldBe("example");
        catalog.TargetIdFor("deployify-target-example", allowDll: false).ShouldBeNull();
        catalog.TargetIdFor("deployify-target-example.dll", allowDll: false).ShouldBeNull();
        catalog.TargetIdFor("deployify-target-example.dll", allowDll: true).ShouldBe("example");
    }

    [Fact]
    public void BuiltInNames_AreNotPlugins()
    {
        var bin = Dir("bin");
        Executable(bin, "deployify-target-nomad");
        Executable(bin, "deployify-target-compose");

        Catalog(new() { ["PATH"] = bin }).Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_ListsEachTargetOnce()
    {
        var a = Dir("a");
        var b = Dir("b");
        Executable(a, "deployify-target-example");
        Executable(b, "deployify-target-example");
        Executable(b, "deployify-target-sample");

        Catalog(new() { ["PATH"] = string.Join(Path.PathSeparator, a, b) })
            .Discover().Select(p => p.TargetId).ShouldBe(["example", "sample"]);
    }

    [Fact]
    public void Factory_FindsPluginTargetsThroughTheSource()
    {
        var bin = Dir("bin");
        Executable(bin, "deployify-target-example");
        var source = new PluginTargetSource(Catalog(new() { ["PATH"] = bin }));
        var factory = new DeploymentTargetFactory([], [source]);

        var target = factory.Get(DeploymentTargetType.Parse("example"));

        target.ShouldBeOfType<RemoteDeploymentTarget>().TargetType.Value.ShouldBe("example");
        factory.Get(DeploymentTargetType.Parse("Example")).ShouldBeSameAs(target);
        factory.IsSupported(DeploymentTargetType.Parse("sample")).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => factory.Get(DeploymentTargetType.Parse("sample")))
            .Message.ShouldContain("deployify plugin list");
    }

    [Fact]
    public void Source_NeverReplacesABuiltInTarget()
    {
        var called = false;
        var source = new PluginTargetSource((_, _) =>
        {
            called = true;
            return new PluginLaunchSpec { ExecutablePath = "x" };
        });

        source.Find(DeploymentTargetType.Compose, null).ShouldBeNull();
        called.ShouldBeFalse();
    }
}
