using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Core.Processes;

/// <summary>Starts external programs. Inject a fake in tests to check the exact arguments.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program to completion and returns its exit code and output. A non-zero exit
    /// code is returned, not thrown. Throws <see cref="ToolNotFoundException"/> when the
    /// program can't be found, <see cref="TimeoutException"/> when <see cref="ProcessSpec.Timeout"/>
    /// is exceeded, and <see cref="OperationCanceledException"/> on cancellation. The process
    /// tree is killed in the last two cases.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Starts the program and yields its output a line at a time, as it is written (for
    /// commands that keep running, like <c>kubectl logs -f</c>). Ends when the program exits;
    /// cancelling <paramref name="ct"/> kills it. A non-zero exit code is not an error: the
    /// program's stderr lines say what went wrong. Throws <see cref="ToolNotFoundException"/>
    /// when the program can't be found. The default runs the program to completion with
    /// <see cref="RunAsync"/> and then yields its output, which is enough for fakes in tests.
    /// </summary>
    IAsyncEnumerable<ProcessOutputLine> StreamAsync(ProcessSpec spec, CancellationToken ct = default)
        => ProcessRunnerExtensions.StreamByRunningAsync(this, spec, ct);
}

/// <summary>One line a program printed, and whether it went to stderr.</summary>
public readonly record struct ProcessOutputLine(string Text, bool IsStdErr);

public static class ProcessRunnerExtensions
{
    /// <summary>The default <see cref="IProcessRunner.StreamAsync"/>: run to completion, then yield stdout and stderr.</summary>
    internal static async IAsyncEnumerable<ProcessOutputLine> StreamByRunningAsync(
        IProcessRunner runner, ProcessSpec spec, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await runner.RunAsync(spec, ct);
        foreach (var line in SplitLines(result.Stdout))
        {
            yield return new ProcessOutputLine(line, false);
        }

        foreach (var line in SplitLines(result.Stderr))
        {
            yield return new ProcessOutputLine(line, true);
        }

        static IEnumerable<string> SplitLines(string text)
            => text.Length == 0 ? [] : text.TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r'));
    }

    public static Task<ProcessResult> RunAsync(
        this IProcessRunner runner, string fileName, IReadOnlyList<string> args, CancellationToken ct = default)
        => runner.RunAsync(new ProcessSpec(fileName, args), ct);

    /// <summary>
    /// True when the program starts and the probe exits with 0. A missing program, a non-zero
    /// exit or a timeout is false; cancellation still throws.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(
        this IProcessRunner runner, string fileName, IReadOnlyList<string> probeArgs,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec(fileName, probeArgs) { Timeout = timeout }, ct);
            return result.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ToolNotFoundException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }
}
