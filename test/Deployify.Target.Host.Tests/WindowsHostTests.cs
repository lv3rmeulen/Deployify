using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Deployify.Target.Host.Tests;

/// <summary>The Windows driver's requests and answers, with PowerShell faked.</summary>
public sealed class WindowsHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WindowsHost Host(FakeProcessRunner fake, Action<HostTargetConfig>? configure = null)
    {
        var config = new HostTargetConfig { Hosts = ["app01"], Os = HostOs.Windows, PowerShell = "pwsh", Transport = HostTransport.Ssh };
        configure?.Invoke(config);
        return new WindowsHost(config, "app01", NullLogger.Instance, fake);
    }

    private static FakeProcessRunner Answering(object result)
        => new() { Default = new ProcessResult(0, "##deployify " + JsonSerializer.Serialize(result), "") };

    private static JsonElement Request(FakeProcessRunner fake, string action)
        => JsonDocument.Parse(fake.Calls.Single(c => c.Args[^1] == action).Stdin!).RootElement;

    [Fact]
    public async Task Apply_SendsTheVariablesWithSecrets_AndTheServiceAccount_OnStdin()
    {
        var output = Fixtures.TempDir();
        var hash = Fixtures.FakePackage(output, "api");
        var release = new HostRelease(
            new HostService
            {
                Service = "api", Name = "prod-api", UnitName = "deployify-prod-api", Environment = "prod",
                Env = new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://*:5100" }
            },
            "f".PadRight(64, '0'), "v1-f", "Api", FolderPackage.PackageDirectory(output, "api"), hash,
            new Dictionary<string, string> { ["DB_PASSWORD"] = Fixtures.Secret });
        var fake = Answering(new { changed = true });

        (await Host(fake, c => { c.ServiceAccount = @"CORP\svc-api$"; c.Root = @"D:\apps"; }).ApplyAsync(release, Ct)).ShouldBeTrue();

        var call = fake.Calls.Single();
        call.FileName.ShouldBe("pwsh");
        call.Args.ShouldContain("-File");
        call.Args.TakeLast(2).ShouldBe(["-Action", "Apply"]);
        call.Sensitive.ShouldBeTrue();
        var request = Request(fake, "Apply");
        request.GetProperty("variables").GetProperty("DB_PASSWORD").GetString().ShouldBe(Fixtures.Secret);
        request.GetProperty("variables").GetProperty("ASPNETCORE_URLS").GetString().ShouldBe("http://*:5100");
        request.GetProperty("service").GetProperty("entryPoint").GetString().ShouldBe("Api");
        request.GetProperty("account").GetString().ShouldBe(@"CORP\svc-api$");
        request.GetProperty("root").GetString().ShouldBe(@"D:\apps");
        request.GetProperty("connection").GetProperty("transport").GetString().ShouldBe("Ssh");
        request.GetProperty("packagePath").GetString().ShouldBe(System.IO.Path.GetFullPath(release.PackageDirectory));
    }

    [Fact]
    public async Task Activate_SendsTheAccount_SoARollbackKeepsIt()
    {
        var fake = Answering(new { changed = false });

        (await Host(fake).ActivateAsync(new ServiceRef("api", "prod-api", "deployify-prod-api", "prod"), "v1-a", Ct)).ShouldBeFalse();

        Request(fake, "Activate").GetProperty("account").GetString().ShouldBe(@"NT AUTHORITY\LocalService");
    }

    [Fact]
    public void ParseList_ReadsOneServiceWrittenAsAnObject()
    {
        var single = JsonDocument.Parse("""
            {"services":{"service":"api","environment":"prod","name":"prod-api","unitName":"deployify-prod-api","fingerprint":"f","release":"r","installed":true}}
            """).RootElement;

        WindowsHost.ParseList(single).Single().ShouldBe(new LiveService("api", "prod-api", "deployify-prod-api", "prod", "f", "r", true));
    }

    [Fact]
    public void ParseLogs_NamesTheHostAndTheSource()
    {
        var result = JsonDocument.Parse("""
            {"lines":[{"text":"started","instance":null,"time":"2026-11-28T10:00:00.0000000Z","error":false},
                      {"text":"terminated unexpectedly","instance":"service-manager","time":"2026-11-28T10:00:01.0000000Z","error":true}]}
            """).RootElement;

        var lines = WindowsHost.ParseLogs(result, "app01");
        var single = WindowsHost.ParseLogs(result, null);

        lines.Select(l => l.Instance).ShouldBe(["app01", "app01/service-manager"]);
        single.Select(l => l.Instance).ShouldBe([null, "service-manager"]);
        lines[1].IsError.ShouldBeTrue();
        lines[0].Timestamp.ShouldNotBeNull();
    }

    [Fact]
    public async Task Check_RequiresAnAdministrator()
    {
        var problems = await Host(Answering(new { windows = true, admin = false })).CheckAsync(Ct);

        problems.Single().ShouldContain("not an administrator");
    }

    [Fact]
    public async Task ThisMachine_NeedsWindows()
    {
        var config = new HostTargetConfig { Os = HostOs.Windows, PowerShell = "pwsh" };
        var reason = await new WindowsHost(config, null, NullLogger.Instance, new FakeProcessRunner()).UnavailableReasonAsync(Ct);

        if (OperatingSystem.IsWindows())
        {
            reason.ShouldBeNull();
        }
        else
        {
            reason!.ShouldContain("host.hosts");
        }
    }
}
