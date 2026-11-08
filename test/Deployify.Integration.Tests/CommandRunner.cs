using System.CommandLine;
using Deployify.Commands;

namespace Deployify.Integration.Tests;

/// <summary>
/// Provides a RootCommand where DI is pre-wired with a mock target,
/// so commands that call DependencyInjection.ResolveAsync work end-to-end
/// against real files on disk but a fake deployment target.
///
/// Strategy: we can't swap out DependencyInjection.ResolveAsync (it's static)
/// so instead we test the command handler behavior by verifying:
///   - exit codes based on real file presence / absence
///   - state file mutations written to disk
///   - that operations called on the mock happened (via NSubstitute)
///
/// For commands that call IDeploymentTarget methods we pass a real state file
/// and manifest.json on disk; the commands call the real Nomad/Kubernetes/Compose
/// target implementations but only through their pure "generate" paths
/// (no network calls) — or we test the path where the state file is missing
/// to verify failure exit codes.
/// </summary>
internal static class CommandRunner
{
    /// <summary>
    /// Returns a fully wired RootCommand (all commands) backed by real DI.
    /// Tests must provide a valid state file and manifest on disk.
    /// </summary>
    public static RootCommand BuildFull() => new("t")
    {
        InitCommand.Build(),
        EnvCommand.Build(),
        GenerateCommand.Build(),
        PlanCommand.Build(),
        ApplyCommand.Build(),
        ValidateCommand.Build(),
        DiffCommand.Build(),
        ExplainCommand.Build(),
        GraphCommand.Build(),
        PublishCommand.Build(),
        PackageCommand.Build(),
        CiCommand.Build(),
        WatchCommand.Build(),
        StatusCommand.Build(),
        RollbackCommand.Build(),
    };
}
