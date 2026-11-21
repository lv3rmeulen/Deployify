using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Commands;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Deployify.Integration.Tests;

/// <summary><c>deployify logs</c>: its options, where its output goes, and how it formats lines.</summary>
[Collection(nameof(ConsoleOutput))]
public sealed class LogsCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DeployifyCli.CreateRootCommand()
            .Parse(["logs", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, Ct);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("--tail", "lots", "--tail takes a number")]
    [InlineData("--tail", "-3", "--tail takes a number")]
    [InlineData("--since", "yesterday", "--since takes a duration")]
    public async Task BadOptions_AreRefusedOnStderr(string option, string value, string message)
    {
        var (code, stdout, stderr) = await RunAsync("api", option, value);

        code.ShouldBe(1);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain(message);
    }

    [Fact]
    public async Task AnUnreachablePlatform_FailsWithTheReason_AndStdoutStaysEmpty()
    {
        using var dir = new TempDir();
        await Harness.WriteStateAsync(dir.Path, new DeployifyStateFile
        {
            ActiveEnvironment = "prod",
            Environments = { ["prod"] = Harness.NomadConfig(address: "http://127.0.0.1:1") }
        }, Ct);

        var (code, stdout, stderr) = await RunAsync("api", "--project-path", dir.Path);

        code.ShouldBe(1);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("Could not read the logs of 'api'");
    }

    [Fact]
    public async Task WithoutAState_ItSaysWhatIsMissing()
    {
        using var dir = new TempDir();
        var (code, stdout, _) = await RunAsync("api", "--project-path", dir.Path);

        code.ShouldBe(1);
        stdout.ShouldBeEmpty();
    }

    [Fact]
    public void EveryBuiltInTarget_ReadsLogs()
    {
        var factory = new ServiceCollection().AddDeployifyCore().BuildServiceProvider().GetRequiredService<IDeploymentTargetFactory>();

        foreach (var id in DeploymentTargetType.BuiltIn)
        {
            factory.Get(id).Supports<IServiceLogSource>().ShouldBeTrue(id.Value);
        }
    }

    [Fact]
    public void TheCommand_IsPartOfTheCli()
        => DeployifyCli.CreateRootCommand().Subcommands.ShouldContain(c => c.Name == "logs");

    [Theory]
    [InlineData("all", null)]
    [InlineData("ALL", null)]
    [InlineData("0", 0)]
    [InlineData("250", 250)]
    public void Tail_TakesANumberOrAll(string text, int? expected)
    {
        LogsCommand.TryParseTail(text, out var tail).ShouldBeTrue();
        tail.ShouldBe(expected);
    }

    [Fact]
    public async Task Text_HasTheInstanceAndOptionallyTheTime()
    {
        var line = new ServiceLogLine
        {
            Text = "hello [not markup]", Instance = "api-1", Timestamp = new DateTimeOffset(2026, 11, 21, 11, 0, 0, 5, TimeSpan.FromHours(1))
        };

        var plain = new StringWriter();
        await new LogsCommand.LogWriter(plain, "api", json: false, timestamps: false, prefix: true, color: false).WriteAsync(line);
        await new LogsCommand.LogWriter(plain, "api", json: false, timestamps: false, prefix: true, color: false).WriteAsync(new ServiceLogLine { Text = "no instance" });
        plain.ToString().ShouldBe($"[api-1] hello [not markup]{Environment.NewLine}no instance{Environment.NewLine}");

        var timed = new StringWriter();
        await new LogsCommand.LogWriter(timed, "api", json: false, timestamps: true, prefix: false, color: false).WriteAsync(line);
        timed.ToString().ShouldBe($"2026-11-21T10:00:00.005Z hello [not markup]{Environment.NewLine}");
    }

    [Fact]
    public async Task Json_IsOneObjectPerLine()
    {
        var output = new StringWriter();
        var writer = new LogsCommand.LogWriter(output, "api", json: true, timestamps: false, prefix: true, color: false);
        await writer.WriteAsync(new ServiceLogLine { Text = "a \"quoted\" line", Instance = "pod-1", IsError = true });
        await writer.WriteAsync(new ServiceLogLine { Text = "b", Timestamp = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero) });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("service").GetString().ShouldBe("api");
        first.RootElement.GetProperty("instance").GetString().ShouldBe("pod-1");
        first.RootElement.GetProperty("stream").GetString().ShouldBe("stderr");
        first.RootElement.GetProperty("text").GetString().ShouldBe("a \"quoted\" line");
        first.RootElement.GetProperty("timestamp").ValueKind.ShouldBe(JsonValueKind.Null);
        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.GetProperty("timestamp").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
        second.RootElement.GetProperty("stream").GetString().ShouldBe("stdout");
    }
}
