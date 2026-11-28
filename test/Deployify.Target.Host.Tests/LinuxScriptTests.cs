using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// Runs deployify-host.sh for real, with bash on this machine, against a temporary root and
/// stand-ins for systemctl, sudo and journalctl that keep the units' state in files. Linux only.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // every test skips there, before touching Unix file modes
public sealed class LinuxScriptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Fixtures.TempDir();
    private string Root => Path.Combine(_dir, "root");
    private string UnitDir => Path.Combine(_dir, "units");
    private string State => Path.Combine(_dir, "state");
    private string Output => Path.Combine(_dir, "out");

    private const string Systemctl = """
        #!/usr/bin/env bash
        state="$STUB_STATE"; mkdir -p "$state"
        echo "systemctl $*" >> "$state/calls"
        cmd="$1"; shift
        case "$cmd" in
          --version) echo "systemd 255" ;;
          link) ln -sfn "$1" "$DEPLOYIFY_UNIT_DIR/$(basename "$1")" ;;
          daemon-reload|reset-failed) ;;
          enable) [ -e "$DEPLOYIFY_UNIT_DIR/$1" ] || { echo "Unit $1 not found." >&2; exit 1; } ;;
          restart)
            u="${1%.service}"
            [ -e "$DEPLOYIFY_UNIT_DIR/$1" ] || { echo "Unit $1 not found." >&2; exit 5; }
            echo active > "$state/$u.active"
            echo "$(readlink "$(dirname "$(readlink "$DEPLOYIFY_UNIT_DIR/$1")")/current")" > "$state/$u.started" ;;
          is-active) u="${1%.service}"; s="$(cat "$state/$u.active" 2>/dev/null || echo inactive)"; echo "$s"; [ "$s" = active ] ;;
          disable) [ "$1" = --now ] && shift; u="${1%.service}"; rm -f "$DEPLOYIFY_UNIT_DIR/$1"; echo inactive > "$state/$u.active" ;;
          show)
            unit=""; props=(); value=no
            while [ $# -gt 0 ]; do
              case "$1" in -p) props+=("$2"); shift 2 ;; --value) value=yes; shift ;; *) unit="$1"; shift ;; esac
            done
            u="${unit%.service}"
            for p in "${props[@]}"; do
              case "$p" in
                LoadState) if [ -e "$DEPLOYIFY_UNIT_DIR/$unit" ]; then v=loaded; else v=not-found; fi ;;
                ActiveState) v="$(cat "$state/$u.active" 2>/dev/null || echo inactive)" ;;
                SubState) v=running ;;
                NRestarts) if [ -f "$state/$u.crash" ]; then v=1; else v=0; fi ;;
                ActiveEnterTimestampMonotonic) v=0 ;;
                ExecMainStatus) v=0 ;;
              esac
              if [ "$value" = yes ]; then echo "$v"; else echo "$p=$v"; fi
            done ;;
          *) echo "unexpected systemctl $cmd" >&2; exit 1 ;;
        esac
        """;

    private const string Sudo = """
        #!/usr/bin/env bash
        [ "$1" = -n ] && shift
        exec "$@"
        """;

    private const string Journalctl = """
        #!/usr/bin/env bash
        echo "journalctl $*" >> "$STUB_STATE/calls"
        echo '{"MESSAGE":"started","PRIORITY":"6","__REALTIME_TIMESTAMP":"1764324000000000"}'
        """;

    public LinuxScriptTests()
    {
        _runner = new StubbedRunner(new Dictionary<string, string?>());
        if (!OperatingSystem.IsLinux())
        {
            return; // every test skips; the stand-ins are bash scripts
        }

        Directory.CreateDirectory(UnitDir);
        Directory.CreateDirectory(State);
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        foreach (var (name, content) in new[] { ("systemctl", Systemctl), ("sudo", Sudo), ("journalctl", Journalctl) })
        {
            var path = Path.Combine(bin, name);
            File.WriteAllText(path, content.ReplaceLineEndings("\n") + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _runner = new StubbedRunner(new Dictionary<string, string?>
        {
            ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"),
            ["DEPLOYIFY_UNIT_DIR"] = UnitDir,
            ["STUB_STATE"] = State
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private readonly StubbedRunner _runner;

    private static void RequireLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "deployify-host.sh runs on Linux.");

    private LinuxHost Host(int keep = 3)
        => new(new HostTargetConfig { Os = HostOs.Linux, Root = Root, KeepReleases = keep, StartupSeconds = 0 }, null,
            NullLogger.Instance, _runner);

    private static readonly HostService Api = new()
    {
        Service = "api", Name = "prod-api", UnitName = "deployify-prod-api", Environment = "prod", KeepReleases = 2,
        Env = new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://*:5100", ["QUOTED"] = "a \"b\" $c" }
    };

    private HostRelease Release(string build)
    {
        var output = Path.Combine(Output, build);
        var hash = Fixtures.FakePackage(output, "api", build);
        var fingerprint = (build + new string('0', 64))[..64];
        return new HostRelease(Api, fingerprint, FolderPackage.ReleaseName("v1", fingerprint), "Api",
            FolderPackage.PackageDirectory(output, "api"), hash, new Dictionary<string, string> { ["DB_PASSWORD"] = Fixtures.Secret });
    }

    private string ServiceDir => Path.Combine(Root, "prod-api");

    private string Current => Path.GetFileName(new DirectoryInfo(Path.Combine(ServiceDir, "current")).LinkTarget!);

    [Fact]
    public async Task Apply_InstallsTheRelease_LinksTheUnit_AndStartsIt()
    {
        RequireLinux();
        var release = Release("aaaa");

        (await Host().ApplyAsync(release, Ct)).ShouldBeTrue();

        Current.ShouldBe(release.Release);
        var releaseDir = Path.Combine(ServiceDir, "releases", release.Release);
        File.ReadAllText(Path.Combine(releaseDir, "app", "Api.dll")).ShouldBe("aaaa");
        File.GetUnixFileMode(Path.Combine(releaseDir, "app", "Api")).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        new FileInfo(Path.Combine(releaseDir, "run")).LinkTarget.ShouldBe("app/Api");

        var env = Path.Combine(releaseDir, "env");
        File.ReadAllText(env).ShouldContain($"DB_PASSWORD=\"{Fixtures.Secret}\"\n");
        File.ReadAllText(env).ShouldContain("QUOTED=\"a \\\"b\\\" \\$c\"\n");
        File.GetUnixFileMode(env).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var unit = File.ReadAllText(Path.Combine(ServiceDir, "deployify-prod-api.service"));
        unit.ShouldContain($"User={Environment.UserName}\n");
        new FileInfo(Path.Combine(UnitDir, "deployify-prod-api.service")).LinkTarget.ShouldBe(Path.Combine(ServiceDir, "deployify-prod-api.service"));
        File.ReadAllText(Path.Combine(State, "deployify-prod-api.started")).Trim().ShouldBe("releases/" + release.Release);
        File.ReadAllText(Path.Combine(State, "calls")).ShouldContain("systemctl daemon-reload");
    }

    [Fact]
    public async Task ApplyingTheSameReleaseAgain_ChangesNothing()
    {
        RequireLinux();
        var release = Release("aaaa");
        await Host().ApplyAsync(release, Ct);
        File.Delete(Path.Combine(State, "calls"));

        (await Host().ApplyAsync(release, Ct)).ShouldBeFalse();

        File.ReadAllText(Path.Combine(State, "calls")).ShouldNotContain("restart");
    }

    [Fact]
    public async Task List_Health_Activate_AndPrune_WorkOnTheReleaseFolders()
    {
        RequireLinux();
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
        Current.ShouldBe(first.Release);
        (await host.ListAsync(Ct)).Single().Fingerprint.ShouldBe(first.Fingerprint);

        await host.ApplyAsync(third, Ct); // keeps 2: the current one and the one that ran before it, though second is newer
        Directory.GetDirectories(Path.Combine(ServiceDir, "releases")).Select(Path.GetFileName).Order()
            .ShouldBe(new[] { first.Release, third.Release }.Order());
        await Should.ThrowAsync<InvalidOperationException>(() => host.ActivateAsync(ServiceRef.Of(Api), second.Release, Ct));
    }

    [Fact]
    public async Task Health_Fails_WhenTheServiceRestartedSinceTheDeploy()
    {
        RequireLinux();
        var release = Release("aaaa");
        await Host().ApplyAsync(release, Ct);
        File.WriteAllText(Path.Combine(State, "deployify-prod-api.crash"), "");

        var health = await Host().HealthAsync(Api, release.Release, Ct);

        health.State.ShouldBe(HealthState.Failed);
        health.Detail!.ShouldContain("keeps exiting");
    }

    [Fact]
    public async Task Prepare_RefusesAFolderDeployifyDidntCreate()
    {
        RequireLinux();
        Directory.CreateDirectory(ServiceDir);
        File.WriteAllText(Path.Combine(ServiceDir, "something.txt"), "mine");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Host().ApplyAsync(Release("aaaa"), Ct));

        ex.Message.ShouldContain("was not deployed by Deployify");
    }

    [Fact]
    public async Task Logs_ReadTheUnitsJournal()
    {
        RequireLinux();
        await Host().ApplyAsync(Release("aaaa"), Ct);

        var lines = await Host().LogsAsync(ServiceRef.Of(Api), new ServiceLogQuery { Service = "api", Tail = 5 }, Ct).ToListAsync(Ct);

        lines.Single().Text.ShouldBe("started");
        File.ReadAllText(Path.Combine(State, "calls")).ShouldContain("journalctl -u deployify-prod-api.service -o json --no-pager -n 5");
    }

    [Fact]
    public async Task Delete_RemovesTheUnitAndTheFolder()
    {
        RequireLinux();
        await Host().ApplyAsync(Release("aaaa"), Ct);

        var removed = await Host().DeleteAsync(ServiceRef.Of(Api), Ct);

        removed.ShouldBe(["systemd unit deployify-prod-api.service", $"folder {ServiceDir}"]);
        Directory.Exists(ServiceDir).ShouldBeFalse();
        File.Exists(Path.Combine(UnitDir, "deployify-prod-api.service")).ShouldBeFalse();
        (await Host().DeleteAsync(ServiceRef.Of(Api), Ct)).ShouldBeEmpty(); // already gone
    }

    [Fact]
    public async Task Ping_ChecksWhatDeployingNeeds()
    {
        RequireLinux();

        (await Host().CheckAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>The real process runner, with the stand-ins first on the PATH.</summary>
    private sealed class StubbedRunner(IReadOnlyDictionary<string, string?> env) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
            => ProcessRunner.Default.RunAsync(spec with { Env = env }, ct);

        public IAsyncEnumerable<ProcessOutputLine> StreamAsync(ProcessSpec spec, CancellationToken ct = default)
            => ProcessRunner.Default.StreamAsync(spec with { Env = env }, ct);
    }
}
