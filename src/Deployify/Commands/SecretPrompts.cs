using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Interactive prompts for credentials (ACL tokens, registry passwords). They ask where the
/// value comes from, not for the value itself, so <c>init</c> and <c>env add</c> don't write
/// secrets into deployify-state.json unless the user explicitly chooses to.
/// </summary>
internal static partial class SecretPrompts
{
    internal enum SourceChoice { DefaultVariable, OtherVariable, PlainText }

    /// <summary>
    /// Asks where the secret <paramref name="label"/> should come from.
    /// Returns the value to store: null (nothing stored, the target reads
    /// <paramref name="defaultEnvVar"/> at run time), <c>env:NAME</c>, or a literal secret when
    /// the user chose and confirmed plain-text storage.
    /// </summary>
    /// <param name="storeDefaultReference">
    /// Store <c>env:DEFAULT</c> instead of null when the default is picked. Needed where an empty
    /// value means "off" rather than "use the well-known variable" (the dashboard token).
    /// </param>
    public static async Task<string?> AskSourceAsync(
        string label, string defaultEnvVar, string directory, CancellationToken ct,
        bool storeDefaultReference = false)
    {
        var isSet = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(defaultEnvVar));
        var status = isSet ? "[green]set[/]" : "[yellow]not set in this shell[/]";

        var prompt = new SelectionPrompt<SourceChoice>()
            .Title($"Where should Deployify get the [bold]{Markup.Escape(label)}[/]?")
            .UseConverter(c => c switch
            {
                SourceChoice.DefaultVariable => $"From the {defaultEnvVar} environment variable ({status})",
                SourceChoice.OtherVariable => "From another environment variable",
                _ => "Save it in deployify-state.json [dim](plain text, not recommended)[/]"
            })
            .AddChoices(SourceChoice.DefaultVariable, SourceChoice.OtherVariable, SourceChoice.PlainText)
            .HighlightStyle(new Style(foreground: Color.Cyan1));

        while (true)
        {
            switch (await ConsolePrompts.PromptAsync(prompt, ct))
            {
                case SourceChoice.DefaultVariable:
                    if (!isSet)
                    {
                        PrintSetHint(defaultEnvVar);
                    }

                    return storeDefaultReference ? Secrets.EnvPrefix + defaultEnvVar : null;

                case SourceChoice.OtherVariable:
                    var name = await new TextPrompt<string>("Environment variable name:")
                        .Validate(n => IsValidVariableName(n.Trim())
                            ? Spectre.Console.ValidationResult.Success()
                            : Spectre.Console.ValidationResult.Error("[red]Use letters, digits and underscores, not starting with a digit.[/]"))
                        .ShowAsync(AnsiConsole.Console, ct);
                    name = StripEnvPrefix(name.Trim());
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                    {
                        PrintSetHint(name);
                    }

                    return Secrets.EnvPrefix + name;

                case SourceChoice.PlainText:
                    var value = await ConsolePrompts.AskSecretAsync($"{Markup.Escape(label)}:", ct);
                    if (string.IsNullOrEmpty(value))
                    {
                        AnsiConsole.MarkupLine("[dim]Nothing entered.[/]");
                        continue;
                    }

                    // Someone pasting "env:NAME" here gets a reference, not a literal.
                    if (!Secrets.IsLiteral(value))
                    {
                        return value;
                    }

                    if (await SecretsWarning.IsTrackedByGitAsync(directory, "deployify-state.json", ct) is true)
                    {
                        AnsiConsole.MarkupLine("[red]![/] deployify-state.json is not git-ignored, so this secret could be committed.");
                    }

                    if (await ConsolePrompts.ConfirmAsync(
                            $"Store the {Markup.Escape(label)} as plain text in deployify-state.json?", defaultValue: false, ct))
                    {
                        return value;
                    }

                    continue;
            }
        }
    }

    /// <summary>True for a name that works as an environment variable on every OS.</summary>
    internal static bool IsValidVariableName(string name)
        => VariableName().IsMatch(StripEnvPrefix(name));

    private static string StripEnvPrefix(string name)
        => name.StartsWith(Secrets.EnvPrefix, StringComparison.Ordinal) ? name[Secrets.EnvPrefix.Length..] : name;

    private static void PrintSetHint(string name)
    {
        AnsiConsole.MarkupLineInterpolated($"  [dim]Set {name} before running plan or apply, e.g.[/]");
        AnsiConsole.MarkupLineInterpolated($"  [dim]  {ShellSnippets.SetVariable(name, "<value>")}[/]");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}

/// <summary>Shell commands for the platform Deployify runs on.</summary>
internal static class ShellSnippets
{
    /// <summary>
    /// A command that sets <paramref name="name"/> persistently for the current user:
    /// PowerShell on Windows, a line for the shell profile elsewhere.
    /// </summary>
    public static string SetVariable(string name, string value) => OperatingSystem.IsWindows()
        ? $"[Environment]::SetEnvironmentVariable('{name}', '{value.Replace("'", "''")}', 'User')"
        : $"export {name}='{value.Replace("'", "'\\''")}'";
}
