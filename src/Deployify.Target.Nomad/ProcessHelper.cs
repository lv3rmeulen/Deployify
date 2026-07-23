using System.Diagnostics;

namespace Deployify.Target.Nomad;

internal static class ProcessHelper
{
    public static async Task RunAsync(
        string command,
        string args,
        CancellationToken ct = default,
        Dictionary<string, string>? env = null)
    {
        var (code, _, stderr) = await RunCoreAsync(command, args, ct, env);
        if (code != 0)
        {
	        throw new InvalidOperationException($"{command} exited with {code}: {stderr}");
        }
    }

    public static async Task<(int Code, string Output)> RunWithOutputAsync(
        string command,
        string args,
        CancellationToken ct = default,
        Dictionary<string, string>? env = null)
    {
        var (code, stdout, stderr) = await RunCoreAsync(command, args, ct, env);
        return (code, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCoreAsync(
        string command,
        string args,
        CancellationToken ct,
        Dictionary<string, string>? env)
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
        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
