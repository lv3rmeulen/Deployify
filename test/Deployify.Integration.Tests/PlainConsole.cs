using System.IO;
using Spectre.Console;

namespace Deployify.Integration.Tests;

/// <summary>A console that writes plain text to a writer, so tests can assert on what a command printed.</summary>
internal static class PlainConsole
{
    /// <summary>
    /// Creates a console without colors or ANSI codes. Spectre recognizes CI runners
    /// (GITHUB_ACTIONS and others) and turns ANSI back on there even with <see cref="AnsiSupport.No"/>,
    /// so the default enrichers are switched off; otherwise tests pass locally and fail on CI.
    /// </summary>
    public static IAnsiConsole Create(TextWriter writer, int width)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        // A redirected writer reports no width, which wraps everything away.
        console.Profile.Width = width;
        return console;
    }
}
