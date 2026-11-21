using System;
using System.IO;
using System.Threading;
using Deployify.Commands.Json;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// For a command whose stdout is its product (<c>deployify logs</c>): everything else it prints,
/// messages and Deployify's own log, goes to stderr, so piping stdout gets nothing but that output.
/// </summary>
internal static class ConsoleRouting
{
    private static readonly AsyncLocal<bool> Reserved = new();

    /// <summary>True while a command has stdout to itself; the console logger then writes to stderr.</summary>
    public static bool StdoutReserved => Reserved.Value;

    /// <summary>Points <see cref="AnsiConsole"/> at <paramref name="error"/> until disposed.</summary>
    public static IDisposable ReserveStdout(TextWriter error)
    {
        var original = AnsiConsole.Console;
        AnsiConsole.Console = JsonOutput.CreateErrorConsole(error);
        Reserved.Value = true;
        return new Restore(original);
    }

    private sealed class Restore(IAnsiConsole original) : IDisposable
    {
        public void Dispose()
        {
            AnsiConsole.Console = original;
            Reserved.Value = false;
        }
    }
}
