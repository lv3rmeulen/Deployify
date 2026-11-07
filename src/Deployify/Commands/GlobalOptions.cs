using System.CommandLine;
using System.IO;
using Deployify.Core.Models;

namespace Deployify.Commands;

/// <summary>
/// Options shared across all commands.
/// </summary>
public static class GlobalOptions
{
    public static readonly Option<string> ProjectPath = new("--project-path", "-p")
    {
        Description = "Path to the Aspire AppHost .csproj or its directory.",
        DefaultValueFactory = _ => Directory.GetCurrentDirectory()
    };

    /// <summary>
    /// Overrides the deployment target. Required — no default.
    /// The environment's configured target is used unless this is specified.
    /// </summary>
    public static readonly Option<DeploymentTargetType?> Target = new("--target", "-t")
    {
        Description = "Deployment target: nomad | kubernetes | compose. Required."
    };

    public static readonly Option<string?> Environment = new("--env", "-e")
    {
        Description = "Environment to use, overriding the active environment (e.g. local, prod)."
    };

    public static readonly Option<bool> NonInteractive = new("--non-interactive")
    {
        Description = "Disable interactive prompts; fail on missing required values.",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> DryRun = new("--dry-run")
    {
        Description = "Simulate the operation without making changes.",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<VerbosityLevel> Verbosity = new("--verbosity", "-v")
    {
        Description = "Log verbosity: quiet | normal | verbose",
        DefaultValueFactory = _ => VerbosityLevel.Normal
    };

    public enum VerbosityLevel
    {
        Quiet,
        Normal,
        Verbose
    }
}
