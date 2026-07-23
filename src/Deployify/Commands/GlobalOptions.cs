using System.CommandLine;
using System.IO;
using Deployify.Core.Models;

namespace Deployify.Commands;

/// <summary>
/// Options shared across all commands.
/// </summary>
public static class GlobalOptions
{
    public static readonly Option<string> ProjectPath = new Option<string>(["--project-path", "-p"], Directory.GetCurrentDirectory, "Path to the Aspire AppHost .csproj or its directory.");

    /// <summary>
    /// Overrides the deployment target. Required — no default.
    /// The environment's configured target is used unless this is specified.
    /// </summary>
    public static readonly Option<DeploymentTargetType?> Target = new Option<DeploymentTargetType?>(["--target", "-t"], "Deployment target: nomad | kubernetes | compose. Required.");

    public static readonly Option<string?> Environment = new Option<string?>(["--env", "-e"], "Environment to use, overriding the active environment (e.g. local, prod).");

    public static readonly Option<bool> NonInteractive = new Option<bool>(["--non-interactive"], () => false, "Disable interactive prompts; fail on missing required values.");

    public static readonly Option<bool> DryRun = new Option<bool>(["--dry-run"], () => false, "Simulate the operation without making changes.");

    public static readonly Option<VerbosityLevel> Verbosity = new Option<VerbosityLevel>(["--verbosity", "-v"], () => VerbosityLevel.Normal, "Log verbosity: quiet | normal | verbose");

    public enum VerbosityLevel { Quiet, Normal, Verbose }
}
