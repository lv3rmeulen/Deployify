using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Core.Services;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Warns when deployify-state.json holds plain-text secrets, and especially when the file
/// would be committed to git.
/// </summary>
internal static class SecretsWarning
{
    public static async Task PrintAsync(DeployifyStateFile state, string directory, CancellationToken ct)
    {
        // Generated parameter values (database passwords etc.) are plain text by necessity.
        if (System.IO.File.Exists(System.IO.Path.Combine(directory, ParameterStore.FileName)) &&
            await IsTrackedByGitAsync(directory, ParameterStore.FileName, ct) is true)
        {
            AnsiConsole.MarkupLine($"\n[red]![/] [cyan]{ParameterStore.FileName}[/] holds generated secrets and is not " +
                                   "git-ignored. Add it to .gitignore so the secrets aren't committed.");
        }

        var literal = StateFileSecrets.FindLiteral(state);
        if (literal.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[yellow]![/] deployify-state.json stores these secrets as plain text:");
        foreach (var path in literal)
        {
            AnsiConsole.MarkupLine($"    [yellow]{Markup.Escape(path)}[/]");
        }

        AnsiConsole.MarkupLine("  Store a reference instead, e.g. [cyan]\"env:NOMAD_TOKEN\"[/], and set the variable " +
                               "in your shell or CI secrets.");

        if (await IsTrackedByGitAsync(directory, "deployify-state.json", ct) is true)
        {
            AnsiConsole.MarkupLine("  [red]This file is not git-ignored.[/] Add [cyan]deployify-state.json[/] and " +
                                   "[cyan]deployify-output/[/] to .gitignore so the secrets aren't committed.");
        }
    }

    /// <summary>True when <paramref name="fileName"/> is inside a git repo and not ignored; null if unknown.</summary>
    private static async Task<bool?> IsTrackedByGitAsync(string directory, string fileName, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in new[] { "check-ignore", "-q", fileName })
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }

            // 0 = ignored, 1 = not ignored, 128 = not a git repository
            return proc.ExitCode switch { 0 => false, 1 => true, _ => null };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
