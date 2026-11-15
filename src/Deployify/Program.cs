using System;
using System.CommandLine;
using Deployify;
using Deployify.Commands;
using Deployify.Core.State;
using Spectre.Console;

// Before anything writes: without a terminal, Spectre.Console would otherwise print nothing.
ConsoleSetup.Configure(AnsiConsole.Console);

var root = DeployifyCli.CreateRootCommand();

// Ctrl+C cancels the running command's token. The default gives a command two seconds to
// stop before the process is killed, which would cut a rollback short; give it time instead.
var configuration = new InvocationConfiguration
{
    ProcessTerminationTimeout = TimeSpan.FromMinutes(30),
    EnableDefaultExceptionHandler = false
};

try
{
    return await root.Parse(args).InvokeAsync(configuration);
}
catch (StateFormatException ex)
{
    // A state file from a newer Deployify: the message says which file and what to do.
    AnsiConsole.MarkupLineInterpolated($"[red]✗[/] {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    // What System.CommandLine's default handler does for everything else.
    if (ex is not OperationCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        configuration.Error.Write("Unhandled exception: ");
        configuration.Error.WriteLine(ex.ToString());
        Console.ResetColor();
    }

    return 1;
}
