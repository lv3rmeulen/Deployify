using System;
using System.Threading.Tasks;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// A spinner around long-running work, shown only in an interactive terminal.
/// </summary>
/// <remarks>
/// Spectre.Console lets one live display (spinner, progress, prompt) run per console at a
/// time and throws for a second one, even when nothing is animated. Without a terminal (CI,
/// redirected output, the test host) a spinner shows nothing useful, so the label is printed
/// as a plain line and the work runs directly. That also means two commands running in the
/// same process, as tests do in parallel, can't make each other fail.
/// </remarks>
internal static class ConsoleStatus
{
    public static async Task<T> RunAsync<T>(string label, Func<Task<T>> work)
    {
        var console = AnsiConsole.Console;
        if (!console.Profile.Capabilities.Interactive)
        {
            console.MarkupLine($"[dim]{Markup.Escape(label)}[/]");
            return await work();
        }

        return await console.Status().Spinner(Spinner.Known.Dots2).StartAsync(label, _ => work());
    }

    public static Task RunAsync(string label, Func<Task> work)
        => RunAsync<object?>(label, async () =>
        {
            await work();
            return null;
        });
}
