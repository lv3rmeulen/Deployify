using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Deployify.SelfUpdate;
using Shouldly;
using Spectre.Console;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary>How Deployify finds out how it was installed, and the <c>dotnet tool update</c> it runs.</summary>
public sealed class SelfUpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string P(params string[] parts) => Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, .. parts]);

    [Fact]
    public void Detect_GlobalTool()
    {
        var home = P("home", "dev");
        var installation = ToolInstallation.Detect(
            P("home", "dev", ".dotnet", "tools", ".store", "deployify", "1.1.0", "deployify", "1.1.0", "tools", "net10.0", "any"), home, home);

        installation.Kind.ShouldBe(ToolInstallKind.Global);
        installation.Location.ShouldBe(P("home", "dev", ".dotnet", "tools"));
    }

    [Fact]
    public void Detect_ToolPath()
    {
        var installation = ToolInstallation.Detect(
            P("opt", "ci-tools", ".store", "deployify", "1.1.0", "deployify", "1.1.0", "tools", "net10.0", "any"), P("work"), P("home", "dev"));

        installation.Kind.ShouldBe(ToolInstallKind.ToolPath);
        installation.Location.ShouldBe(P("opt", "ci-tools"));
    }

    [Fact]
    public void Detect_LocalTool_FindsTheManifestThatListsDeployify()
    {
        using var repo = new TempDir();
        var nested = Directory.CreateDirectory(Path.Combine(repo.Path, "src", "AppHost")).FullName;
        File.WriteAllText(Path.Combine(nested, "dotnet-tools.json"), """{ "version": 1, "tools": { "dotnet-ef": {} } }""");
        Directory.CreateDirectory(Path.Combine(repo.Path, ".config"));
        var manifest = Path.Combine(repo.Path, ".config", "dotnet-tools.json");
        File.WriteAllText(manifest, """{ "version": 1, "isRoot": true, "tools": { "deployify": { "version": "1.1.0", "commands": ["deployify"] } } }""");

        var installation = ToolInstallation.Detect(P("home", "dev", ".nuget", "packages", "deployify", "1.1.0", "tools", "net10.0", "any"), nested, P("home", "dev"));

        installation.Kind.ShouldBe(ToolInstallKind.Local);
        installation.Location.ShouldBe(manifest);
    }

    [Fact]
    public void Detect_FromTheNuGetCacheWithoutAManifest_IsNotInstalled()
    {
        using var dir = new TempDir();
        ToolInstallation.Detect(P("home", "dev", ".nuget", "packages", "deployify", "1.1.0", "tools", "net10.0", "any"), dir.Path, P("home", "dev"))
            .Kind.ShouldBe(ToolInstallKind.NotInstalled);
    }

    [Fact]
    public void Detect_ABuild_IsUnknown()
        => ToolInstallation.Detect(P("src", "Deployify", "src", "Deployify", "bin", "Debug", "net10.0"), P("src"), P("home", "dev"))
            .Kind.ShouldBe(ToolInstallKind.Unknown);

    [Fact]
    public void PickVersion_StaysOnTheMajorVersion_AndIgnoresPreReleases()
    {
        string[] available = ["1.0.0", "1.1.0", "1.2.0-beta.1", "2.0.0"];
        SelfUpdatePlan.PickVersion("1.0.0", available, includePrerelease: false, crossMajor: false).ShouldBe("1.1.0");
        SelfUpdatePlan.PickVersion("1.0.0", available, includePrerelease: true, crossMajor: false).ShouldBe("1.2.0-beta.1");
        SelfUpdatePlan.PickVersion("1.0.0", available, includePrerelease: false, crossMajor: true).ShouldBe("2.0.0");
        SelfUpdatePlan.PickVersion("2.0.0", available, includePrerelease: false, crossMajor: true).ShouldBeNull();
        SelfUpdatePlan.NewerMajor("1.0.0", available, includePrerelease: false).ShouldBe("2.0.0");
        SelfUpdatePlan.NewerMajor("2.0.0", available, includePrerelease: false).ShouldBeNull();
    }

    [Fact]
    public void Plan_Global_IsDetachedOnWindowsOnly()
    {
        var global = new ToolInstallation(ToolInstallKind.Global, P("tools"));

        var windows = SelfUpdatePlan.For(global, "1.2.0", PackageVersionSource.DefaultSource, windows: true, dotnet: "dotnet")!;
        windows.Args.ShouldBe(["tool", "update", "--global", "deployify", "--version", "1.2.0"]);
        windows.Detach.ShouldBeTrue();

        SelfUpdatePlan.For(global, "1.2.0", PackageVersionSource.DefaultSource, windows: false, dotnet: "dotnet")!.Detach.ShouldBeFalse();
    }

    [Fact]
    public void Plan_ToolPathAndLocal_PassTheirLocation_AndAnotherFeed()
    {
        var toolPath = SelfUpdatePlan.For(new ToolInstallation(ToolInstallKind.ToolPath, "/opt/tools"), "1.2.0", "/feed", windows: true, dotnet: "dotnet")!;
        toolPath.Args.ShouldBe(["tool", "update", "--tool-path", "/opt/tools", "deployify", "--version", "1.2.0", "--add-source", "/feed"]);
        toolPath.Detach.ShouldBeTrue();

        // A local tool's new version goes to a new folder, so nothing is locked: no need to wait.
        var local = SelfUpdatePlan.For(new ToolInstallation(ToolInstallKind.Local, "/repo/dotnet-tools.json"), "1.2.0", PackageVersionSource.DefaultSource, windows: true, dotnet: "dotnet")!;
        local.Args.ShouldBe(["tool", "update", "deployify", "--tool-manifest", "/repo/dotnet-tools.json", "--version", "1.2.0"]);
        local.Detach.ShouldBeFalse();
    }

    [Fact]
    public void Plan_NotATool_IsNull()
    {
        SelfUpdatePlan.For(new ToolInstallation(ToolInstallKind.Unknown, "/src"), "1.2.0", PackageVersionSource.DefaultSource, windows: false).ShouldBeNull();
        SelfUpdatePlan.For(new ToolInstallation(ToolInstallKind.NotInstalled, "/cache"), "1.2.0", PackageVersionSource.DefaultSource, windows: false).ShouldBeNull();
    }

    [Fact]
    public void BackgroundScript_WaitsForDeployifyToExit_AndQuotesEverything()
    {
        var plan = SelfUpdatePlan.For(new ToolInstallation(ToolInstallKind.ToolPath, @"C:\Users\o'brien\tools"), "1.2.0",
            PackageVersionSource.DefaultSource, windows: true, dotnet: @"C:\Program Files\dotnet\dotnet.exe")!;

        var script = plan.BackgroundScript(4242, @"C:\Temp\deployify-update.log");

        script.ShouldBe(string.Join('\n',
            "Wait-Process -Id 4242 -Timeout 120 -ErrorAction SilentlyContinue",
            @"& 'C:\Program Files\dotnet\dotnet.exe' 'tool' 'update' '--tool-path' 'C:\Users\o''brien\tools' 'deployify' '--version' '1.2.0' *> 'C:\Temp\deployify-update.log'",
            "exit $LASTEXITCODE"));
        Encoding.Unicode.GetString(Convert.FromBase64String(SelfUpdatePlan.Encode(script))).ShouldBe(script);
    }

    [Fact]
    public async Task Versions_FromAFolderOfPackages()
    {
        using var feed = new TempDir();
        foreach (var name in new[] { "deployify.1.0.0.nupkg", "deployify.1.1.0-beta.1.nupkg", "deployify.1.1.0.snupkg", "deployify.hosting.1.1.0.nupkg" })
        {
            File.WriteAllText(Path.Combine(feed.Path, name), "");
        }

        (await new PackageVersionSource().GetVersionsAsync(feed.Path, "deployify", Ct)).Order().ShouldBe(["1.0.0", "1.1.0-beta.1"]);
    }

    [Fact]
    public async Task Versions_FromAV3Feed_ThroughItsFlatContainer()
    {
        var http = new HttpClient(new Feed());
        (await new PackageVersionSource(http).GetVersionsAsync("https://feed.test/v3/index.json", "Deployify", Ct)).ShouldBe(["1.0.0", "1.1.0"]);
        (await new PackageVersionSource(http).GetVersionsAsync("https://feed.test/v3/index.json", "missing", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Versions_FromAFeedThatIsntOne_SaysSo()
        => (await Should.ThrowAsync<SelfUpdateException>(
                new PackageVersionSource(new HttpClient(new Feed())).GetVersionsAsync("https://feed.test/broken.json", "deployify", Ct)))
            .Message.ShouldContain("PackageBaseAddress");

    private sealed class Feed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://feed.test/v3/index.json" => Json("""{ "resources": [ { "@id": "https://feed.test/flat", "@type": "PackageBaseAddress/3.0.0" } ] }"""),
                "https://feed.test/broken.json" => Json("""{ "resources": [] }"""),
                "https://feed.test/flat/deployify/index.json" => Json("""{ "versions": ["1.0.0", "1.1.0"] }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary><c>deployify update</c> and <c>deployify plugin update</c> through the CLI.</summary>
[Collection(nameof(ConsoleOutput))]
public sealed class UpdateCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAnsiConsole _original = AnsiConsole.Console;
    private readonly StringWriter _out = new();

    public UpdateCommandTests() => AnsiConsole.Console = PlainConsole.Create(_out, width: 400);

    public void Dispose() => AnsiConsole.Console = _original;

    private static RootCommand Root() => new("t") { UpdateCommand.Build(), PluginCommand.Build() };

    [Fact]
    public async Task PluginUpdate_OutsideAProject_Fails()
    {
        using var dir = new TempDir();
        (await Root().Parse(["plugin", "update", "--project-path", dir.Path]).InvokeAsync(cancellationToken: Ct)).ShouldBe(1);
        _out.ToString().ShouldContain("Not in a Deployify project");
    }

    [Fact]
    public async Task PluginUpdate_WithNothingLocked_HasNothingToDo()
    {
        using var dir = new TempDir();
        await new StateManager().SaveFileAsync(new DeployifyStateFile(), dir.Path, Ct);

        (await Root().Parse(["plugin", "update", "--project-path", dir.Path, "--check"]).InvokeAsync(cancellationToken: Ct)).ShouldBe(0);
        _out.ToString().ShouldContain("nothing to update");
    }

    [Fact]
    public async Task PluginUpdate_OfATargetThatIsntLocked_Fails()
    {
        using var dir = new TempDir();
        await new StateManager().SaveFileAsync(new DeployifyStateFile(), dir.Path, Ct);

        (await Root().Parse(["plugin", "update", "digitalocean", "--project-path", dir.Path]).InvokeAsync(cancellationToken: Ct)).ShouldBe(1);
        _out.ToString().ShouldContain("deployify plugin install digitalocean");
    }

    [Fact]
    public async Task UpdateSelf_Check_ExitsTwoWhenThereIsANewerVersion()
    {
        using var feed = new TempDir();
        File.WriteAllText(Path.Combine(feed.Path, "deployify.999.0.0.nupkg"), "");
        File.WriteAllText(Path.Combine(feed.Path, $"deployify.{CliVersion.Current}.nupkg"), "");

        (await Root().Parse(["update", "--self", "--check", "--major", "--source", feed.Path]).InvokeAsync(cancellationToken: Ct))
            .ShouldBe(PluginCommand.UpdatesAvailableExitCode);
        _out.ToString().ShouldContain("→ 999.0.0");
    }

    [Fact]
    public async Task UpdateSelf_StaysOnTheMajorVersion_AndSaysANewOneIsOut()
    {
        using var feed = new TempDir();
        File.WriteAllText(Path.Combine(feed.Path, "deployify.999.0.0.nupkg"), "");

        (await Root().Parse(["update", "--self", "--check", "--source", feed.Path]).InvokeAsync(cancellationToken: Ct)).ShouldBe(0);
        _out.ToString().ShouldContain("Deployify 999.0.0 is out and may have breaking changes");
        _out.ToString().ShouldContain("is up to date");
    }

    [Fact]
    public async Task Source_WithoutSelf_IsRefused()
        => (await Root().Parse(["update", "--source", "/feed"]).InvokeAsync(cancellationToken: Ct)).ShouldBe(1);

    [Fact]
    public void HostingVersion_FromThePackageReference()
    {
        using var dir = new TempDir();
        var csproj = Path.Combine(dir.Path, "AppHost.csproj");
        File.WriteAllText(csproj, """<Project Sdk="Aspire.AppHost.Sdk/13.0.0"><ItemGroup><PackageReference Include="Deployify.Hosting" Version="1.1.0" /></ItemGroup></Project>""");

        UpdateCommand.FindHostingVersion(dir.Path).ShouldBe((csproj, "1.1.0", "AppHost.csproj"));
    }

    [Fact]
    public void HostingVersion_FromCentralPackageManagement()
    {
        using var dir = new TempDir();
        var app = Directory.CreateDirectory(Path.Combine(dir.Path, "src", "AppHost")).FullName;
        var csproj = Path.Combine(app, "AppHost.csproj");
        File.WriteAllText(csproj, """<Project><ItemGroup><PackageReference Include="deployify.hosting" /></ItemGroup></Project>""");
        var props = Path.Combine(dir.Path, "Directory.Packages.props");
        File.WriteAllText(props, """<Project><ItemGroup><PackageVersion Include="Deployify.Hosting" Version="1.0.2" /></ItemGroup></Project>""");

        UpdateCommand.FindHostingVersion(csproj).ShouldBe((csproj, "1.0.2", props));
    }

    [Fact]
    public void HostingVersion_NotReferencedAsAPackage_IsNull()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "AppHost.csproj"),
            """<Project><ItemGroup><ProjectReference Include="..\Deployify.Hosting\Deployify.Hosting.csproj" /></ItemGroup></Project>""");

        UpdateCommand.FindHostingVersion(dir.Path).ShouldBeNull();
    }
}
