using System;
using Spectre.Console;

namespace Deployify;

/// <summary>Makes the console usable before any command writes to it.</summary>
internal static class ConsoleSetup
{
    /// <summary>
    /// Line width when there's no terminal and <c>COLUMNS</c> isn't set. Wide enough that nothing
    /// wraps: CI logs keep each message on one line, and tables and panels size to their content.
    /// </summary>
    internal const int DefaultWidth = 1000;

    /// <summary>
    /// Gives <paramref name="console"/> a usable width when it has none. With no terminal at all,
    /// as on most CI runners, Spectre.Console 0.57.2 reports a width of -1 and then writes
    /// nothing, not even plain lines. This sets the width to <c>COLUMNS</c> if that's a positive
    /// number, or <see cref="DefaultWidth"/>. A console that already has a width is left alone,
    /// so this does nothing in a terminal, or once Spectre fixes the width itself.
    /// </summary>
    public static void Configure(IAnsiConsole console, Func<string, string?>? getEnvironmentVariable = null)
    {
        if (console.Profile.Width > 0)
        {
            return;
        }

        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        console.Profile.Width = int.TryParse(getEnvironmentVariable("COLUMNS"), out var columns) && columns > 0
            ? columns
            : DefaultWidth;
    }
}
