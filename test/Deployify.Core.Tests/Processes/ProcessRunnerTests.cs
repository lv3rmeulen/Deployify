using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Processes;

/// <summary>Runs real programs, so these use POSIX tools and skip on Windows.</summary>
public sealed class ProcessRunnerTests
{
    private readonly ProcessRunner _sut = ProcessRunner.Default;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipOnWindows()
        => Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX tools; the Windows paths are covered by ProcessStartInfoTests.");

    [Fact]
    public async Task Arguments_ReachTheProgramIntact()
    {
        SkipOnWindows();
        string[] tricky = ["two words", "a\"quote", "it's", "$HOME", "a;b&c|d", "*", "", "jsonpath={.items[*].metadata.name}"];

        var result = await _sut.RunAsync("printf", ["%s\\n", .. tricky], Ct);

        result.Ok.ShouldBeTrue(result.Stderr);
        result.Stdout.Split('\n')[..tricky.Length].ShouldBe(tricky);
    }

    [Fact]
    public async Task Stdin_IsWrittenAndClosed()
    {
        SkipOnWindows();
        var result = await _sut.RunAsync(new ProcessSpec("cat", []) { Stdin = "{\"kind\":\"Secret\"}" }, Ct);

        result.Stdout.Trim().ShouldBe("{\"kind\":\"Secret\"}");
    }

    [Fact]
    public async Task Env_AddsAndRemovesVariables()
    {
        SkipOnWindows();
        var spec = new ProcessSpec("sh", ["-c", "printf '%s|%s' \"$DEPLOYIFY_X\" \"${HOME:-unset}\""])
        {
            Env = new Dictionary<string, string?> { ["DEPLOYIFY_X"] = "a b", ["HOME"] = null }
        };

        (await _sut.RunAsync(spec, Ct)).Stdout.Trim().ShouldBe("a b|unset");
    }

    [Fact]
    public async Task WorkingDirectory_IsUsed()
    {
        SkipOnWindows();
        var dir = Path.GetTempPath();
        var result = await _sut.RunAsync(new ProcessSpec("pwd", []) { WorkingDirectory = dir }, Ct);

        Path.GetFullPath(result.Stdout.Trim()).TrimEnd('/').ShouldBe(Path.GetFullPath(dir).TrimEnd('/'));
    }

    [Fact]
    public async Task NonZeroExit_IsReturnedWithStderr()
    {
        SkipOnWindows();
        var result = await _sut.RunAsync("sh", ["-c", "echo out; echo oops >&2; exit 3"], Ct);

        result.ExitCode.ShouldBe(3);
        result.Ok.ShouldBeFalse();
        result.Stdout.Trim().ShouldBe("out");
        result.Stderr.Trim().ShouldBe("oops");
        result.OutputOrError.Trim().ShouldBe("oops");
        Should.Throw<InvalidOperationException>(() => result.EnsureSuccess("thing")).Message.ShouldBe("thing exited with 3: oops");
    }

    [Fact]
    public async Task MissingProgram_ThrowsToolNotFound()
    {
        var ex = await Should.ThrowAsync<ToolNotFoundException>(
            () => _sut.RunAsync("deployify-no-such-tool", [], Ct));

        ex.Tool.ShouldBe("deployify-no-such-tool");
    }

    [Fact]
    public async Task Timeout_KillsTheProcessAndThrows()
    {
        SkipOnWindows();
        var started = DateTime.UtcNow;

        await Should.ThrowAsync<TimeoutException>(
            () => _sut.RunAsync(new ProcessSpec("sleep", ["30"]) { Timeout = TimeSpan.FromMilliseconds(200) }, Ct));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_KillsTheProcessAndThrowsCanceled()
    {
        SkipOnWindows();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.RunAsync("sleep", ["30"], cts.Token));
    }

    [Fact]
    public async Task Output_IsLoggedLineByLineWithTag()
    {
        SkipOnWindows();
        var log = new ListLogger();

        await _sut.RunAsync(new ProcessSpec("sh", ["-c", "echo one; echo two >&2"]) { Log = log, LogTag = "kubectl" }, Ct);

        log.Lines.ShouldContain("[kubectl] one");
        log.Lines.ShouldContain("[kubectl stderr] two");
    }

    [Fact]
    public async Task Sensitive_LogsNothing()
    {
        SkipOnWindows();
        var log = new ListLogger();

        var result = await _sut.RunAsync(
            new ProcessSpec("sh", ["-c", "echo s3cret; echo s3cret >&2"]) { Log = log, Sensitive = true }, Ct);

        result.Stdout.ShouldContain("s3cret");
        log.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task IsAvailable_TrueOnZeroExit_FalseWhenMissingOrFailing()
    {
        SkipOnWindows();
        (await _sut.IsAvailableAsync("true", [], ct: Ct)).ShouldBeTrue();
        (await _sut.IsAvailableAsync("false", [], ct: Ct)).ShouldBeFalse();
        (await _sut.IsAvailableAsync("deployify-no-such-tool", [], ct: Ct)).ShouldBeFalse();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>How a <see cref="ProcessSpec"/> becomes a ProcessStartInfo, including on Windows.</summary>
public sealed class ProcessStartInfoTests
{
    private const string WinPath = @"C:\Windows\system32;C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin;C:\tools";

    [Fact]
    public void Posix_UsesArgumentListVerbatim()
    {
        var psi = ProcessRunner.CreateStartInfo(new ProcessSpec("kubectl", ["get", "x y", "-o", "jsonpath={.a}"]),
            isWindows: false, path: "/usr/bin", pathExt: null, fileExists: _ => true);

        psi.FileName.ShouldBe("kubectl");
        psi.Arguments.ShouldBeEmpty();
        psi.ArgumentList.ShouldBe(["get", "x y", "-o", "jsonpath={.a}"]);
        psi.UseShellExecute.ShouldBeFalse();
        psi.RedirectStandardInput.ShouldBeFalse();
    }

    [Fact]
    public void Windows_BareNameResolvingToExe_UsesArgumentList()
    {
        var exe = Path.Combine(@"C:\tools", "kubectl.exe");
        var psi = ProcessRunner.CreateStartInfo(new ProcessSpec("kubectl", ["apply", "-f", @"C:\my dir\a.yaml"]),
            isWindows: true, WinPath, ".COM;.EXE;.BAT;.CMD", f => f == exe);

        psi.FileName.ShouldBe(exe);
        psi.ArgumentList.ShouldBe(["apply", "-f", @"C:\my dir\a.yaml"]);
    }

    [Fact]
    public void Windows_BatchFile_RunsThroughCmdWithEscapedArguments()
    {
        var cmd = Path.Combine(@"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin", "az.cmd");
        var psi = ProcessRunner.CreateStartInfo(new ProcessSpec("az", ["storage", "--name", "a&b \"c\" 100%"]),
            isWindows: true, WinPath, ".COM;.EXE;.BAT;.CMD", f => f == cmd);

        psi.FileName.ShouldEndWith("cmd.exe", Case.Insensitive);
        psi.ArgumentList.ShouldBeEmpty();
        psi.Arguments.ShouldBe(
            $"/d /e:ON /v:OFF /s /c \"\"{cmd}\" \"storage\" \"--name\" \"a&b \"\"c\"\" 100%%cd:~,%\"\"");
    }

    [Fact]
    public void ResolveOnPath_FollowsPathOrderThenPathExtOrder()
    {
        var found = new HashSet<string>
        {
            Path.Combine(@"C:\tools", "az.cmd"),
            Path.Combine(@"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin", "az.cmd")
        };

        ProcessRunner.ResolveOnPath("az", WinPath, null, found.Contains)
            .ShouldBe(Path.Combine(@"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin", "az.cmd"));
    }

    [Fact]
    public void ResolveOnPath_PrefersExeOverCmdInTheSameDirectory()
    {
        var found = new HashSet<string> { Path.Combine(@"C:\tools", "aws.cmd"), Path.Combine(@"C:\tools", "aws.exe") };

        ProcessRunner.ResolveOnPath("aws", @"C:\tools", ".COM;.EXE;.BAT;.CMD", found.Contains)
            .ShouldBe(Path.Combine(@"C:\tools", "aws.exe"));
    }

    [Theory]
    [InlineData("az.cmd")]
    [InlineData(@"C:\tools\az")]
    [InlineData("./tool")]
    public void ResolveOnPath_LeavesExplicitNamesAlone(string name)
        => ProcessRunner.ResolveOnPath(name, WinPath, null, _ => true).ShouldBeNull();

    [Fact]
    public void ResolveOnPath_ReturnsNullWhenNotFound()
        => ProcessRunner.ResolveOnPath("nomad", WinPath, null, _ => false).ShouldBeNull();

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("x&calc", "\"x&calc\"")]
    [InlineData("^|<>()", "\"^|<>()\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("%PATH%", "\"%%cd:~,%PATH%%cd:~,%\"")]
    [InlineData("!x!", "\"!x!\"")]
    [InlineData(@"C:\dir\", @"""C:\dir\\""")]
    [InlineData(@"a\""b", @"""a\\""""b""")]
    public void BatchCommandLine_QuotesEachArgument(string arg, string expected)
        => ProcessRunner.BuildBatchCommandLine("x.cmd", [arg])
            .ShouldBe($"/d /e:ON /v:OFF /s /c \"\"x.cmd\" {expected}\"");

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\0b")]
    public void BatchCommandLine_RefusesLineBreaks(string arg)
        => Should.Throw<ArgumentException>(() => ProcessRunner.BuildBatchCommandLine("x.cmd", [arg]));

    [Fact]
    public void Stdin_RedirectsInput_AndEnvIsApplied()
    {
        var psi = ProcessRunner.CreateStartInfo(
            new ProcessSpec("az", ["x"])
            {
                Stdin = "{}",
                Env = new Dictionary<string, string?> { ["AZURE_STORAGE_CONNECTION_STRING"] = "secret" }
            },
            isWindows: false, null, null, _ => false);

        psi.RedirectStandardInput.ShouldBeTrue();
        psi.Environment["AZURE_STORAGE_CONNECTION_STRING"].ShouldBe("secret");
    }

    [Fact]
    public void ProcessResult_Output_FallsBackToStderr()
    {
        new ProcessResult(1, "  ", "err").Output.ShouldBe("err");
        new ProcessResult(0, "out", "err").Output.ShouldBe("out");
    }
}
