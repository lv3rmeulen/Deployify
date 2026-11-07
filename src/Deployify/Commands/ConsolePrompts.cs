using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Async versions of Spectre's <c>AnsiConsole.Ask</c> / <c>Confirm</c> / <c>Prompt</c>, so
/// waiting for user input honors the command's cancellation token (Ctrl+C).
/// </summary>
internal static class ConsolePrompts
{
    public static Task<T> AskAsync<T>(string prompt, CancellationToken ct)
        => new TextPrompt<T>(prompt).ShowAsync(AnsiConsole.Console, ct);

    public static Task<T> AskAsync<T>(string prompt, T defaultValue, CancellationToken ct)
        => new TextPrompt<T>(prompt).DefaultValue(defaultValue).ShowAsync(AnsiConsole.Console, ct);

    public static Task<bool> ConfirmAsync(string prompt, bool defaultValue, CancellationToken ct)
        => new ConfirmationPrompt(prompt) { DefaultValue = defaultValue }.ShowAsync(AnsiConsole.Console, ct);

    public static Task<bool> ConfirmAsync(string prompt, CancellationToken ct)
        => ConfirmAsync(prompt, defaultValue: true, ct);

    public static Task<T> PromptAsync<T>(IPrompt<T> prompt, CancellationToken ct)
        => prompt.ShowAsync(AnsiConsole.Console, ct);

    /// <summary>
    /// Prompts for a secret without echoing it. An empty answer is allowed. Users can type
    /// <c>env:VAR_NAME</c> to store a reference instead of the secret itself.
    /// </summary>
    public static Task<string> AskSecretAsync(string prompt, CancellationToken ct)
        => new TextPrompt<string>(prompt).Secret().AllowEmpty().ShowAsync(AnsiConsole.Console, ct);
}
