using Deployify.Commands.Json;
using Deployify.Core.Deployments;
using Spectre.Console;

namespace Deployify.Commands;

/// <summary>
/// Writes deployment messages to the console in the CLI's usual style. With <c>--output json</c>
/// they also go into the document's <c>diagnostics</c>.
/// </summary>
internal sealed class ConsoleDeploymentReporter : IDeploymentReporter
{
    public static readonly ConsoleDeploymentReporter Instance = new();

    public void Info(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[dim]{message}[/]");
        JsonOutput.Current?.Report(JsonSeverity.Info, message);
    }

    public void Warn(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[yellow]![/] {message}");
        JsonOutput.Current?.Report(JsonSeverity.Warning, message);
    }

    public void Error(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {message}");
        JsonOutput.Current?.Report(JsonSeverity.Error, message);
    }
}
