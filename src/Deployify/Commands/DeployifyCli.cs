using System.CommandLine;

namespace Deployify.Commands;

/// <summary>
/// Builds the <c>deployify</c> command tree. Shared by <c>Program</c> and the docs generator
/// (tools/Deployify.DocGen), so the CLI reference is always generated from the real commands.
/// </summary>
public static class DeployifyCli
{
    public static RootCommand CreateRootCommand() =>
        new("deployify – deploy .NET Aspire AppHost projects to any target (Nomad, Kubernetes, Compose, …)")
        {
            InitCommand.Build(),
            EnvCommand.Build(),
            GenerateCommand.Build(),
            ValidateCommand.Build(),
            PlanCommand.Build(),
            ApplyCommand.Build(),
            PublishCommand.Build(),
            PackageCommand.Build(),
            PromoteCommand.Build(),
            CiCommand.Build(),
            WatchCommand.Build(),
            ExplainCommand.Build(),
            GraphCommand.Build(),
            DiffCommand.Build(),
            StatusCommand.Build(),
            RollbackCommand.Build(),
            DestroyCommand.Build(),
            StateCommand.Build(),
            PluginCommand.Build(),
        };
}
