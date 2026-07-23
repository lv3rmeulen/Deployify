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
};

return await root.InvokeAsync(args);
