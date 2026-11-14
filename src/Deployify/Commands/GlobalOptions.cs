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
        Description = TargetOption.Description,
        CustomParser = TargetOption.ParseOptional
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

    public static readonly Option<bool> Strict = new("--strict")
    {
        Description = "Fail when the manifest has resources Deployify won't deploy, even if nothing depends on them " +
                      "(list the ones you expect under ignoreResources).",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> RequireDigest = new("--require-digest")
    {
        Description = "Fail instead of deploying a built image by tag when no published digest is recorded for it " +
                      "(for example after 'deployify promote').",
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
