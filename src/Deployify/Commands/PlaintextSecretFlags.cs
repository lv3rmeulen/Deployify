using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Deployify.Core.Models;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Rejects literal secrets passed as command-line flags (<c>--nomad-token abc</c>). They would
/// be written to deployify-state.json and also end up in shell history and CI logs. Flags take
/// an <c>env:NAME</c> reference instead, or can be left out so the target reads its well-known
/// variable. <c>--allow-plaintext-secrets</c> keeps the old behavior.
/// </summary>
internal static class PlaintextSecretFlags
{
    public static readonly Option<bool> AllowOption = new("--allow-plaintext-secrets")
    {
        Description = "Allow secret flags (tokens, passwords) to take literal values, which are " +
                      "saved as plain text in deployify-state.json. By default they must be env:NAME references."
    };

    /// <summary>
    /// Prints an error and returns false when any of <paramref name="secretFlags"/> holds a
    /// literal value and <see cref="AllowOption"/> isn't set.
    /// </summary>
    /// <param name="secretFlags">
    /// Each flag with the variable to suggest, and whether the target reads that variable on its
    /// own when the flag is left out.
    /// </param>
    public static bool Check(ParseResult parseResult, params (Option<string?> Flag, string EnvVar, bool ReadWhenOmitted)[] secretFlags)
    {
        if (parseResult.GetValue(AllowOption))
        {
            return true;
        }

        var literal = Find(parseResult, secretFlags);
        if (literal.Count == 0)
        {
            return true;
        }

        AnsiConsole.MarkupLine("[red]✗[/] These flags hold secrets and would be saved as plain text in deployify-state.json:");
        foreach (var (flag, envVar, readWhenOmitted) in literal)
        {
            var fallback = readWhenOmitted ? $", or leave it out to use {envVar}" : "";
            AnsiConsole.MarkupLineInterpolated($"    [yellow]{flag.Name}[/]  [dim]→ use {flag.Name} env:{envVar}{fallback}[/]");
        }

        AnsiConsole.MarkupLine("  Pass [cyan]--allow-plaintext-secrets[/] to store them anyway.");
        return false;
    }

    internal static List<(Option<string?> Flag, string EnvVar, bool ReadWhenOmitted)> Find(
        ParseResult parseResult, IEnumerable<(Option<string?> Flag, string EnvVar, bool ReadWhenOmitted)> secretFlags)
        => secretFlags.Where(f => Secrets.IsLiteral(parseResult.GetValue(f.Flag))).ToList();
}
