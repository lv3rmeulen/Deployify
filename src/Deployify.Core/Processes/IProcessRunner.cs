using System;
using System.Collections.Generic;
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
}

public static class ProcessRunnerExtensions
{
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
