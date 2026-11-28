using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Host.Tests;

/// <summary>
/// Runs deployify-host.ps1 for real with PowerShell 7, on this machine, with the Windows-only
/// commands (the service manager, the registry, ACLs, the event log) replaced by the stand-ins in
/// WindowsScriptMocks.ps1. Skipped where pwsh isn't installed.
/// </summary>
public sealed class WindowsScriptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Fixtures.TempDir();
    private string Root => Path.Combine(_dir, "root");
    private string StateFile => Path.Combine(_dir, "mock-state.json");
    private string ServiceDir => Path.Combine(Root, "prod-api");

    private static readonly string? s_pwsh = FindPwsh();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // ignore
        }
    }

    private static string? FindPwsh()
    {
        foreach (var candidate in new[] { "pwsh", "/opt/pwsh/pwsh", "/usr/bin/pwsh" })
        {
            try
            {
                if (ProcessRunner.Default.RunAsync(new ProcessSpec(candidate, ["-NoProfile", "-Command", "exit 0"])
                        { Timeout = TimeSpan.FromSeconds(30) }, Ct).GetAwaiter().GetResult().Ok)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ToolNotFoundException or TimeoutException or InvalidOperationException)
            {
                // ignore
            }
        }

        return null;
    }

    private static void RequirePwsh() => Assert.SkipWhen(s_pwsh is null, "PowerShell 7 (pwsh) is not installed.");

    /// <summary>The script with the stand-ins inserted into its server block, ahead of the actions.</summary>
    private string MockedScript()
    {
        const string anchor = "    switch ($ServerAction) {";
        var script = WindowsHost.Script.ReplaceLineEndings("\n");
        script.Split(anchor).Length.ShouldBe(2, "the script's action switch moved; update WindowsScriptTests");
        var mocks = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WindowsScriptMocks.ps1")).ReplaceLineEndings("\n");
        var path = Path.Combine(_dir, "deployify-host.mocked.ps1");
        File.WriteAllText(path, script.Replace(anchor, mocks + "\n" + anchor));
        return path;
    }

    private WindowsHost Host(int keep = 3)
        => new(new HostTargetConfig { Os = HostOs.Windows, Root = Root, PowerShell = s_pwsh, KeepReleases = keep, StartupSeconds = 0 },
            null, NullLogger.Instance, new MockedRunner(StateFile))
        {
            ScriptPath = MockedScript()
        };

    private static readonly HostService Api = new()
    {
        Service = "api", Name = "prod-api", UnitName = "deployify-prod-api", Environment = "prod", KeepReleases = 2,
        Env = new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://*:5100" }
    };

    private HostRelease Release(string build, bool failsToStart = false)
    {
        var output = Path.Combine(_dir, "out", build);
        Fixtures.FakePackage(output, "api", build);
        var package = FolderPackage.PackageDirectory(output, "api");
        File.WriteAllText(Path.Combine(package, "Api.exe"), failsToStart ? "fail-to-start" : "app " + build);
        var hash = FolderPackage.ComputePackageHash(package);
        var fingerprint = (build + new string('0', 64))[..64];
        return new HostRelease(Api, fingerprint, FolderPackage.ReleaseName("v1", fingerprint), "Api", package, hash,
            new Dictionary<string, string> { ["DB_PASSWORD"] = Fixtures.Secret });
    }

    private JsonObject State() => JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();

    private JsonObject Service() => State()["services"]!["deployify-prod-api"]!.AsObject();

    private JsonObject Marker() => JsonNode.Parse(File.ReadAllText(Path.Combine(ServiceDir, "deployify.json")))!.AsObject();

    private static string ExePath(string release) => Path.Combine("prod-api", "releases", release, "app", "Api.exe");

    [Fact]
    public async Task Apply_InstallsTheService_WithItsVariables_AndStartsIt()
    {
        RequirePwsh();
        var release = Release("aaaa");

        (await Host().ApplyAsync(release, Ct)).ShouldBeTrue();

        var service = Service();
        service["state"]!.GetValue<string>().ShouldBe("Running");
        service["path"]!.GetValue<string>().ShouldEndWith(ExePath(release.Release) + "\"");
        service["account"]!.GetValue<string>().ShouldBe(@"NT AUTHORITY\LocalService");
        service["display"]!.GetValue<string>().ShouldBe("api (prod, Deployify)");
        service["environment"]!.AsArray().Select(e => e!.GetValue<string>())
            .ShouldBe(["ASPNETCORE_URLS=http://*:5100", $"DB_PASSWORD={Fixtures.Secret}"]);
        (await File.ReadAllTextAsync(Path.Combine(ServiceDir, "releases", release.Release, "app", "Api.dll"), Ct)).ShouldBe("aaaa");
        (await File.ReadAllTextAsync(Path.Combine(ServiceDir, "settings", release.Release + ".json"), Ct)).ShouldContain(Fixtures.Secret);
        Marker()["release"]!.GetValue<string>().ShouldBe(release.Release);
        Marker()["processId"]!.GetValue<int>().ShouldBe(Environment.ProcessId);
        var calls = State()["calls"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        calls.ShouldContain("protect deployify-prod-api");
        calls.ShouldContain("event source Api");
        calls.ShouldContain(c => c.StartsWith("sc failure deployify-prod-api reset= 86400", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyingTheSameReleaseAgain_ChangesNothing()
    {
        RequirePwsh();
        var release = Release("aaaa");
        await Host().ApplyAsync(release, Ct);

        (await Host().ApplyAsync(release, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_ExplainsAnAppThatDoesntRunAsAService()
    {
        RequirePwsh();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Host().ApplyAsync(Release("aaaa", failsToStart: true), Ct));

        ex.Message.ShouldContain("did not start");
        ex.Message.ShouldContain("UseWindowsService()");
    }

    [Fact]
    public async Task List_Health_Activate_AndPrune_WorkOnTheReleaseFolders()
    {
        RequirePwsh();
        var host = Host();
        var first = Release("aaaa");
        var second = Release("bbbb");
        var third = Release("cccc");

        await host.ApplyAsync(first, Ct);
        await host.ApplyAsync(second, Ct);

        (await host.ListAsync(Ct)).Single().ShouldBe(
            new LiveService("api", "prod-api", "deployify-prod-api", "prod", second.Fingerprint, second.Release, true));
        (await host.HealthAsync(Api, second.Release, Ct)).State.ShouldBe(HealthState.Healthy);
        (await host.HealthAsync(Api, first.Release, Ct)).Detail!.ShouldContain($"not {first.Release}");

        (await host.ActivateAsync(ServiceRef.Of(Api), first.Release, Ct)).ShouldBeTrue();
        Service()["path"]!.GetValue<string>().ShouldEndWith(ExePath(first.Release) + "\"");
        Marker()["fingerprint"]!.GetValue<string>().ShouldBe(first.Fingerprint);

        await host.ApplyAsync(third, Ct); // keeps 2: the current one and the one that ran before it, though second is newer
        Directory.GetDirectories(Path.Combine(ServiceDir, "releases")).Select(Path.GetFileName).Order()
            .ShouldBe(new[] { first.Release, third.Release }.Order());
        File.Exists(Path.Combine(ServiceDir, "settings", second.Release + ".json")).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => host.ActivateAsync(ServiceRef.Of(Api), second.Release, Ct));
    }

    [Fact]
    public async Task Health_Fails_WhenTheServiceRestartedSinceTheDeploy_OrStopped()
    {
        RequirePwsh();
        var release = Release("aaaa");
        await Host().ApplyAsync(release, Ct);
        var state = State();
        state["services"]!["deployify-prod-api"]!["processId"] = Environment.ProcessId + 1;
        await File.WriteAllTextAsync(StateFile, state.ToJsonString(), Ct);

        var restarted = await Host().HealthAsync(Api, release.Release, Ct);

        state["services"]!["deployify-prod-api"]!["state"] = "Stopped";
        state["services"]!["deployify-prod-api"]!["exitCode"] = 1067;
        await File.WriteAllTextAsync(StateFile, state.ToJsonString(), Ct);
        var stopped = await Host().HealthAsync(Api, release.Release, Ct);

        restarted.State.ShouldBe(HealthState.Failed);
        restarted.Detail!.ShouldContain("restarted");
        stopped.State.ShouldBe(HealthState.Failed);
        stopped.Detail!.ShouldContain("exit code 1067");
    }

    [Fact]
    public async Task Prepare_RefusesAFolderDeployifyDidntCreate()
    {
        RequirePwsh();
        Directory.CreateDirectory(ServiceDir);
        await File.WriteAllTextAsync(Path.Combine(ServiceDir, "something.txt"), "mine", Ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Host().ApplyAsync(Release("aaaa"), Ct));

        ex.Message.ShouldContain("was not deployed by Deployify");
    }

    [Fact]
    public async Task Logs_ReadTheAppsEvents_AndTheServiceManagersAboutTheService()
    {
        RequirePwsh();
        await Host().ApplyAsync(Release("aaaa"), Ct);
        var state = State();
        state["events"] = JsonNode.Parse("""
            [
              { "log": "Application", "provider": "Api", "recordId": 10, "time": "2026-11-28T10:00:00Z", "message": "Request failed", "level": 2 },
              { "log": "System", "provider": "Service Control Manager", "recordId": 20, "time": "2026-11-28T10:00:01Z",
                "message": "The api (prod, Deployify) service terminated unexpectedly.", "level": 2 },
              { "log": "System", "provider": "Service Control Manager", "recordId": 21, "time": "2026-11-28T10:00:02Z",
                "message": "The Print Spooler service entered the running state.", "level": 4 }
            ]
            """);
        await File.WriteAllTextAsync(StateFile, state.ToJsonString(), Ct);

        var lines = await Host().LogsAsync(ServiceRef.Of(Api), new ServiceLogQuery { Service = "api" }, Ct).ToListAsync(Ct);

        lines.Select(l => (l.Text, l.Instance)).ShouldBe(
        [
            ("Request failed", null),
            ("The api (prod, Deployify) service terminated unexpectedly.", "service-manager")
        ]);
        lines.ShouldAllBe(l => l.IsError && l.Timestamp != null);
    }

    [Fact]
    public async Task Delete_RemovesTheServiceAndTheFolder()
    {
        RequirePwsh();
        await Host().ApplyAsync(Release("aaaa"), Ct);

        var removed = await Host().DeleteAsync(ServiceRef.Of(Api), Ct);

        removed.ShouldBe(["Windows service deployify-prod-api", $"folder {ServiceDir}"]);
        Directory.Exists(ServiceDir).ShouldBeFalse();
        State()["services"]!.AsObject().ShouldNotContainKey("deployify-prod-api");
        (await Host().DeleteAsync(ServiceRef.Of(Api), Ct)).ShouldBeEmpty(); // already gone
    }

    [Fact]
    public async Task Ping_RunsTheScript()
    {
        RequirePwsh();

        var problems = await Host().CheckAsync(Ct);

        if (OperatingSystem.IsWindows())
        {
            problems.ShouldBeEmpty(); // the stand-in says administrator
        }
        else
        {
            problems.Single().ShouldContain("doesn't run Windows");
        }
    }

    /// <summary>The real process runner, telling the stand-ins where their state is.</summary>
    private sealed class MockedRunner(string stateFile) : IProcessRunner
    {
        private readonly Dictionary<string, string?> _env = new()
        {
            ["DEPLOYIFY_MOCK_STATE"] = stateFile,
            ["DEPLOYIFY_MOCK_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
            => ProcessRunner.Default.RunAsync(spec with { Env = _env }, ct);
    }
}
