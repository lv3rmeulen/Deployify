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

    public static readonly Option<bool> RequireSignature = new("--require-signature")
    {
        Description = "Fail unless every image the environment builds is pinned by digest and has a cosign signature " +
                      "that the environment's signing settings trust (signing.publicKey, or signing.identity and signing.issuer). " +
                      "Needs cosign.",
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> AllowUnverified = new("--allow-unverified")
    {
        Description = "Start target plugins that deployify-plugins.lock.json has no hash for, with a warning each time " +
                      "(a plugin whose files don't match its locked hash is still refused). " +
                      "Run 'deployify plugin lock' instead to verify them.",
        DefaultValueFactory = _ => false
    };

    /// <summary>
    /// <c>--output json</c>: one JSON document on stdout, in the schema documented under
    /// reference/json-output, and the human-readable output on stderr instead.
    /// See <see cref="Json.JsonOutput"/>.
    /// </summary>
    public static readonly Option<OutputFormat> Output = new("--output", "-o")
    {
        Description = "Output format: text | json. With json, stdout gets one JSON document (see the JSON output " +
                      "reference) and the human-readable output goes to stderr.",
        DefaultValueFactory = _ => OutputFormat.Text
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

/// <summary>What <see cref="GlobalOptions.Output"/> accepts.</summary>
public enum OutputFormat
{
    /// <summary>Human-readable output on stdout (the default).</summary>
    Text,

    /// <summary>One JSON document on stdout; human-readable output on stderr.</summary>
    Json
}
