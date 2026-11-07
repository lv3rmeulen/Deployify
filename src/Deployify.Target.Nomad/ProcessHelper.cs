using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.Nomad;

internal static class ProcessHelper
{
    public static async Task RunAsync(
        string command,
        string args,
        Dictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var (code, _, stderr) = await RunCoreAsync(command, args, env, ct);
        if (code != 0)
        {
            throw new InvalidOperationException($"{command} exited with {code}: {stderr}");
        }
    }

    public static async Task<(int Code, string Output)> RunWithOutputAsync(
        string command,
        string args,
        Dictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var (code, stdout, stderr) = await RunCoreAsync(command, args, env, ct);
        return (code, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCoreAsync(
        string command,
        string args,
        Dictionary<string, string>? env,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"Could not start {command}.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already exited */
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (proc.ExitCode, stdout, stderr);
    }
}
