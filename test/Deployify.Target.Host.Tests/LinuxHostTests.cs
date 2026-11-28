using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Host.Tests;

/// <summary>The Linux driver's plumbing, with ssh faked.</summary>
public sealed class LinuxHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LinuxHost Host(FakeProcessRunner fake, Action<HostTargetConfig>? configure = null, string? host = "web01")
    {
        var config = new HostTargetConfig { Hosts = host is null ? [] : [host], Os = HostOs.Linux };
        configure?.Invoke(config);
        return new LinuxHost(config, host, NullLogger.Instance, fake);
    }

    private static string Answer(params (string Key, string Value)[] lines)
        => string.Join('\n', lines.Select(l => $"##deployify {l.Key} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(l.Value))}")) + "\n";

    [Fact]
    public void Program_CarriesEveryValue_ThroughBase64()
    {
        var program = LinuxHost.Program("apply", new Dictionary<string, string> { ["env"] = "A=\"x'y $z\"\n" }, "/srv/apps");

        program.ShouldStartWith("DEPLOYIFY_ACTION=apply\n");
        program.ShouldContain("arg_env=\"$(printf '%s' 'QT0ieCd5ICR6Igo=' | base64 -d; printf x)\"");
        program.ShouldContain("arg_root=");
        program.ShouldNotContain("x'y $z");
        program.ShouldContain("case \"$DEPLOYIFY_ACTION\" in");
    }

    [Fact]
    public void ParseResult_DecodesRepeatedKeys_AndIgnoresOtherOutput()
    {
        var result = LinuxHost.ParseResult("Welcome to web01\n" + Answer(("removed", "unit a"), ("removed", "folder /x"), ("changed", "")));

        result["removed"].ShouldBe(["unit a", "folder /x"]);
        result["changed"].ShouldBe([""]);
    }

    [Fact]
    public void ParseMarker_ReadsTheMarkerFile()
    {
        var live = LinuxHost.ParseMarker("managedBy=deployify\nservice=api\nenvironment=prod\nname=prod-api\nunit=deployify-prod-api\n" +
                                         "fingerprint=abc\nrelease=v1-abc\ninstalled=yes\n");

        live.ShouldBe(new LiveService("api", "prod-api", "deployify-prod-api", "prod", "abc", "v1-abc", true));
        LinuxHost.ParseMarker("managedBy=deployify\n").ShouldBeNull();
    }

    [Fact]
    public async Task Actions_RunOverSsh_WithTheConfiguredConnection()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(0, "", "") };

        await Host(fake, c => { c.Port = 2222; c.UserName = "deploy"; c.KeyFilePath = "/keys/id"; }).ListAsync(Ct);

        var call = fake.Calls.Single();
        call.FileName.ShouldBe("ssh");
        call.Args.ShouldBe(["-o", "BatchMode=yes", "-o", "ConnectTimeout=20", "-p", "2222", "-i", "/keys/id", "-l", "deploy", "web01", "bash -s"]);
        call.Stdin!.ShouldContain("DEPLOYIFY_ACTION=list");
    }

    [Fact]
    public async Task Apply_UploadsThePackageWithScp_OnlyWhenTheHostDoesntHaveIt()
    {
        var output = Fixtures.TempDir();
        var hash = Fixtures.FakePackage(output, "api");
        var release = new HostRelease(
            new HostService { Service = "api", Name = "prod-api", UnitName = "deployify-prod-api", Environment = "prod" },
            "f".PadRight(64, '0'), "v1-f00000000000", "Api", FolderPackage.PackageDirectory(output, "api"), hash,
            new Dictionary<string, string> { ["DB_PASSWORD"] = Fixtures.Secret });
        var fake = new FakeProcessRunner()
            .When(s => s.Stdin?.Contains("DEPLOYIFY_ACTION=prepare") == true,
                _ => new ProcessResult(0, Answer(("upload", "yes"), ("incoming", "/opt/deployify/prod-api/incoming/v1.tar.gz")), ""))
            .When(s => s.Stdin?.Contains("DEPLOYIFY_ACTION=apply") == true, _ => new ProcessResult(0, Answer(("changed", "yes")), ""));

        var changed = await Host(fake).ApplyAsync(release, Ct);

        changed.ShouldBeTrue();
        var scp = fake.Calls.Single(c => c.FileName == "scp");
        scp.Args[^2].ShouldBe(Path.GetFileName(release.ArchivePath));
        scp.Args[^1].ShouldBe("web01:/opt/deployify/prod-api/incoming/v1.tar.gz");
        scp.WorkingDirectory.ShouldBe(Path.GetDirectoryName(release.ArchivePath));
        File.Exists(release.ArchivePath).ShouldBeTrue();
        var apply = fake.Calls.Single(c => c.Stdin?.Contains("DEPLOYIFY_ACTION=apply") == true);
        apply.Sensitive.ShouldBeTrue();
        apply.Stdin!.ShouldNotContain(Fixtures.Secret); // base64-encoded on stdin, never on a command line
    }

    [Fact]
    public async Task Apply_SkipsTheUpload_ForAReleaseTheHostHas()
    {
        var output = Fixtures.TempDir();
        var hash = Fixtures.FakePackage(output, "api");
        var release = new HostRelease(
            new HostService { Service = "api", Name = "prod-api", UnitName = "deployify-prod-api" },
            "f".PadRight(64, '0'), "v1-f", "Api", FolderPackage.PackageDirectory(output, "api"), hash, new Dictionary<string, string>());
        var fake = new FakeProcessRunner()
            .When(s => s.Stdin?.Contains("DEPLOYIFY_ACTION=prepare") == true, _ => new ProcessResult(0, Answer(("upload", "no")), ""))
            .When(s => s.Stdin?.Contains("DEPLOYIFY_ACTION=apply") == true, _ => new ProcessResult(0, Answer(("changed", "no")), ""));

        (await Host(fake).ApplyAsync(release, Ct)).ShouldBeFalse();

        fake.Calls.ShouldNotContain(c => c.FileName == "scp");
    }

    [Fact]
    public async Task AFailedAction_SaysWhichHost()
    {
        var fake = new FakeProcessRunner { Default = new ProcessResult(255, "", "ssh: connect to host web01 port 22: Connection refused") };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Host(fake).ListAsync(Ct));

        ex.Message.ShouldBe("list on web01 failed: ssh: connect to host web01 port 22: Connection refused");
    }

    [Fact]
    public async Task Check_ReportsWhatTheHostLacks()
    {
        var fake = new FakeProcessRunner
        {
            Default = new ProcessResult(0, Answer(("os", "Linux"), ("user", "deploy"), ("systemd", "yes"), ("sudo", "no"), ("root", "no"),
                ("tar", "yes"), ("curl", "yes"), ("dotnet", "no")), "")
        };

        var problems = await Host(fake, c => c.SelfContained = false).CheckAsync(Ct);

        problems.Count.ShouldBe(3);
        problems[0].ShouldContain("deploy can't run 'sudo -n systemctl'");
        problems[1].ShouldContain("can't write to /opt/deployify");
        problems[2].ShouldContain("no .NET runtime");
    }

    [Theory]
    [InlineData("*", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void HealthHost_IsLoopback_UnlessTheServiceListensOnOneAddress(string listen, string expected)
        => LinuxHost.HealthHost(listen).ShouldBe(expected);

    [Fact]
    public void ParseJournalLine_ReadsTextTimeAndPriority()
    {
        var line = LinuxHost.ParseJournalLine(
            """{"MESSAGE":"boom","PRIORITY":"3","__REALTIME_TIMESTAMP":"1764324000123456"}""", "web01")!;
        var binary = LinuxHost.ParseJournalLine("""{"MESSAGE":[104,105],"PRIORITY":"6"}""", null)!;

        line.Text.ShouldBe("boom");
        line.IsError.ShouldBeTrue();
        line.Instance.ShouldBe("web01");
        line.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1764324000123));
        binary.Text.ShouldBe("hi");
        binary.IsError.ShouldBeFalse();
        LinuxHost.ParseJournalLine("-- No entries --", null).ShouldBeNull();
    }

    [Fact]
    public void Unit_StartsTheCurrentRelease_AsTheDeployUser()
    {
        var unit = SystemdUnit.Render(new HostService { Service = "api", Name = "prod-api", Environment = "prod" }, "/opt/deployify/", "deploy");

        unit.ShouldContain("User=deploy\n");
        unit.ShouldContain("WorkingDirectory=/opt/deployify/prod-api/current/app\n");
        unit.ShouldContain("EnvironmentFile=/opt/deployify/prod-api/current/env\n");
        unit.ShouldContain("ExecStart=/opt/deployify/prod-api/current/run\n");
        unit.ShouldContain("Restart=on-failure\n");
        unit.ShouldContain("WantedBy=multi-user.target\n");
    }

    [Fact]
    public void EnvironmentFile_EscapesWhatSystemdWouldInterpret()
    {
        var env = SystemdUnit.RenderEnvironment([new("B", "say \"hi\" $HOME `x` \\n"), new("A", "plain")]);

        env.ShouldBe("A=\"plain\"\nB=\"say \\\"hi\\\" \\$HOME \\`x\\` \\\\n\"\n");
    }
}
