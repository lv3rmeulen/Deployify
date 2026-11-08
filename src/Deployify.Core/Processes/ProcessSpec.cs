using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Deployify.Core.Processes;

/// <summary>
/// What to run. Arguments are always a list: every item reaches the program as exactly one
/// argument, whatever spaces, quotes or shell characters it contains. There is deliberately
/// no way to pass a pre-built command line.
/// </summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args)
{
    /// <summary>Text written to the process's standard input, which is then closed.</summary>
    public string? Stdin { get; init; }

    /// <summary>Extra environment variables. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Env { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Kills the process and throws <see cref="TimeoutException"/> when exceeded.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Logger for debug output, one line per output line, prefixed with <see cref="LogTag"/>.</summary>
    public ILogger? Log { get; init; }

    /// <summary>Prefix for logged lines; defaults to the program name.</summary>
    public string? LogTag { get; init; }

    /// <summary>
    /// The output may hold secrets: nothing the process prints is logged. Use it for anything
    /// that reads or writes secret values.
    /// </summary>
    public bool Sensitive { get; init; }
}
