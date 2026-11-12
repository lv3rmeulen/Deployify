using System;
using System.CommandLine;
using Deployify;
using Deployify.Commands;
using Spectre.Console;

// Before anything writes: without a terminal, Spectre.Console would otherwise print nothing.
ConsoleSetup.Configure(AnsiConsole.Console);

var root = DeployifyCli.CreateRootCommand();

// Ctrl+C cancels the running command's token. The default gives a command two seconds to
// stop before the process is killed, which would cut a rollback short; give it time instead.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration
{
    ProcessTerminationTimeout = TimeSpan.FromMinutes(30)
});
