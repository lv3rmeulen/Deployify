using Deployify.Core.Deployments;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>Writes deployment messages to the console in the CLI's usual style.</summary>
internal sealed class ConsoleDeploymentReporter : IDeploymentReporter
{
    public static readonly ConsoleDeploymentReporter Instance = new();

    public void Info(string message) => AnsiConsole.MarkupLineInterpolated($"[dim]{message}[/]");

    public void Warn(string message) => AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {message}");

    public void Error(string message) => AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {message}");
}
