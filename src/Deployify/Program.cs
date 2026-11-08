using System;
using System.CommandLine;
using Deployify.Commands;

var root = new RootCommand("deployify – deploy .NET Aspire AppHost projects to any target (Nomad, Kubernetes, Compose, …)")
{
    InitCommand.Build(),
    EnvCommand.Build(),
    GenerateCommand.Build(),
    ValidateCommand.Build(),
    PlanCommand.Build(),
    ApplyCommand.Build(),
    PublishCommand.Build(),
    PackageCommand.Build(),
    CiCommand.Build(),
    WatchCommand.Build(),
    ExplainCommand.Build(),
    GraphCommand.Build(),
    DiffCommand.Build(),
    StatusCommand.Build(),
    RollbackCommand.Build(),
    DestroyCommand.Build(),
    StateCommand.Build(),
};

// Ctrl+C cancels the running command's token. The default gives a command two seconds to
// stop before the process is killed, which would cut a rollback short; give it time instead.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration
{
    ProcessTerminationTimeout = TimeSpan.FromMinutes(30)
});
