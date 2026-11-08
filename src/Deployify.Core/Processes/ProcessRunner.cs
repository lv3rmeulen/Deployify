using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Processes;

/// <summary>
/// The one place Deployify starts external programs (kubectl, docker, az, aws, gcloud,
/// dotnet, git). No shell is involved, so arguments reach the program intact; the one
/// exception is a Windows batch file such as <c>az.cmd</c>, which only runs through
/// <c>cmd.exe</c> and gets its own escaping (see <see cref="BuildBatchCommandLine"/>).
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static ProcessRunner Default { get; } = new();

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(spec);
        var tag = spec.LogTag ?? Path.GetFileNameWithoutExtension(spec.FileName);
        var log = spec.Sensitive ? null : spec.Log;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // ENOENT / ERROR_FILE_NOT_FOUND
        {
            throw new ToolNotFoundException(spec.FileName, ex);
        }

        using (proc)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => Collect(stdout, e.Data, log, $"[{tag}]");
            proc.ErrorDataReceived += (_, e) => Collect(stderr, e.Data, log, $"[{tag} stderr]");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (spec.Timeout is { } limit)
            {
                timeout.CancelAfter(limit);
            }

            try
            {
                if (spec.Stdin is not null)
                {
                    try
                    {
                        await proc.StandardInput.WriteAsync(spec.Stdin.AsMemory(), timeout.Token);
                        proc.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The program exited without reading all of its input; its exit code says why.
                    }
                }

                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                if (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"'{spec.FileName}' did not finish within {spec.Timeout}.");
                }

                throw;
            }

            return new ProcessResult(proc.ExitCode, Read(stdout), Read(stderr));
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
        => CreateStartInfo(spec, OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), File.Exists);

    internal static ProcessStartInfo CreateStartInfo(
        ProcessSpec spec, bool isWindows, string? path, string? pathExt, Func<string, bool> fileExists)
    {
        foreach (var arg in spec.Args)
        {
            ArgumentNullException.ThrowIfNull(arg, nameof(spec.Args));
        }

        var fileName = isWindows ? ResolveOnPath(spec.FileName, path, pathExt, fileExists) ?? spec.FileName : spec.FileName;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.Stdin is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (isWindows && IsBatchFile(fileName))
        {
            // CreateProcess runs .cmd/.bat files through cmd.exe, which re-parses the command
            // line by its own rules; ArgumentList's quoting doesn't protect against that.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = BuildBatchCommandLine(fileName, spec.Args);
        }
        else
        {
            psi.FileName = fileName;
            foreach (var arg in spec.Args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (spec.WorkingDirectory is not null)
        {
            psi.WorkingDirectory = spec.WorkingDirectory;
        }

        if (spec.Env is not null)
        {
            foreach (var (key, value) in spec.Env)
            {
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }

        return psi;
    }

    /// <summary>
    /// Finds a bare program name on PATH the way cmd.exe does: per directory, each PATHEXT
    /// extension in turn. Needed because Process.Start only appends ".exe", so "az" (az.cmd)
    /// would otherwise not be found. Returns null when the name already has a path or an
    /// extension, or isn't on PATH.
    /// </summary>
    internal static string? ResolveOnPath(string fileName, string? path, string? pathExt, Func<string, bool> fileExists)
    {
        if (fileName.IndexOfAny(['\\', '/']) >= 0 || Path.HasExtension(fileName) || string.IsNullOrEmpty(path))
        {
            return null;
        }

        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName + ext.ToLowerInvariant());
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static bool IsBatchFile(string fileName)
        => fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Arguments for <c>cmd.exe</c> that run a batch file with each argument intact. Every
    /// argument is quoted, so <c>&amp; | &lt; &gt; ^ ( )</c> are literal; quotes are doubled;
    /// <c>%</c> becomes <c>%%cd:~,%</c>, which cmd expands back to a single <c>%</c> without
    /// expanding a variable; delayed expansion (<c>!</c>) is switched off. Line breaks can't be
    /// passed to a batch file safely at all, so they're refused.
    /// </summary>
    internal static string BuildBatchCommandLine(string batchFile, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder("/d /e:ON /v:OFF /s /c \"");
        AppendBatchArg(sb, batchFile);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendBatchArg(sb, arg);
        }

        return sb.Append('"').ToString();
    }

    private static void AppendBatchArg(StringBuilder sb, string arg)
    {
        if (arg.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Arguments to a batch file can't contain line breaks or NUL characters.", nameof(arg));
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    sb.Append(c);
                    continue;
                case '"':
                    // Backslashes right before a quote are doubled so the program's own
                    // argument parser keeps them as backslashes.
                    sb.Append('\\', backslashes).Append("\"\"");
                    break;
                case '%':
                    sb.Append("%%cd:~,%");
                    break;
                default:
                    sb.Append(c);
                    break;
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes).Append('"');
    }

    private static void Collect(StringBuilder buffer, string? line, ILogger? log, string prefix)
    {
        if (line is null)
        {
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(line);
        }

        log?.LogDebug("{Prefix} {Line}", prefix, line);
    }

    private static string Read(StringBuilder buffer)
    {
        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (Win32Exception)
        {
            // already exiting
        }
    }
}
