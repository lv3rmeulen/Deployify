using System;
using System.CommandLine;
using Deployify.Commands;

var root = DeployifyCli.CreateRootCommand();

// Ctrl+C cancels the running command's token. The default gives a command two seconds to
// stop before the process is killed, which would cut a rollback short; give it time instead.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration
{
    ProcessTerminationTimeout = TimeSpan.FromMinutes(30)
});
