using System;

namespace Deployify.Core.Processes;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Stdout on success, stderr on failure.</summary>
    public string OutputOrError => Ok ? Stdout : Stderr;

    /// <summary>Stdout, or stderr when stdout is empty (how the CLI clients report results).</summary>
    public string Output => string.IsNullOrWhiteSpace(Stdout) ? Stderr : Stdout;

    /// <summary>Stderr, or stdout when stderr is empty.</summary>
    public string ErrorOrOutput => string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr;

    /// <summary>
    /// Throws when the program exited non-zero, with its stderr (or stdout when stderr is
    /// empty: dotnet/MSBuild print their errors there).
    /// </summary>
    public ProcessResult EnsureSuccess(string what)
        => Ok ? this : throw new InvalidOperationException($"{what} exited with {ExitCode}: {ErrorOrOutput.Trim()}");
}
