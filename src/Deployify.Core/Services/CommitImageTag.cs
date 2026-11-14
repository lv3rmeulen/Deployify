using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;

namespace Deployify.Core.Services;

/// <summary>An image tag taken from the commit being built, and where it came from.</summary>
/// <param name="Tag">The full commit SHA, lowercase.</param>
/// <param name="Source">The CI variable or "git" that supplied it, for the console output.</param>
public sealed record CommitTag(string Tag, string Source);

/// <summary>
/// Works out an immutable image tag for a CI deploy from the commit being built, so every
/// build pushes a new image and a rollback can return to the previous one. CI variables are
/// checked first; outside CI the tag comes from <c>git rev-parse HEAD</c>, but only when the
/// working tree has no uncommitted changes (otherwise two different builds would share a tag).
/// </summary>
public static class CommitImageTag
{
    /// <summary>CI variables that hold the commit SHA, in the order they are checked.</summary>
    public static readonly IReadOnlyList<string> CommitVariables =
    [
        "GITHUB_SHA",          // GitHub Actions
        "BUILD_SOURCEVERSION", // Azure Pipelines
        "CI_COMMIT_SHA",       // GitLab CI
        "BITBUCKET_COMMIT",    // Bitbucket Pipelines
        "GIT_COMMIT"           // Jenkins (git plugin)
    ];

    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The commit tag, or null when no CI variable holds a commit SHA and git can't supply a
    /// clean one. <paramref name="reason"/> then says why, for the error message.
    /// </summary>
    public static async Task<(CommitTag? Tag, string? Reason)> ResolveAsync(
        string directory,
        Func<string, string?>? getEnvironmentVariable = null,
        IProcessRunner? runner = null,
        CancellationToken ct = default)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        runner ??= ProcessRunner.Default;

        foreach (var name in CommitVariables)
        {
            if (Normalize(getEnvironmentVariable(name)) is { } sha)
            {
                return (new CommitTag(sha, name), null);
            }
        }

        try
        {
            var head = await runner.RunAsync(new ProcessSpec("git", ["rev-parse", "HEAD"])
            {
                WorkingDirectory = directory, Timeout = s_gitTimeout
            }, ct);
            if (!head.Ok || Normalize(head.Stdout) is not { } sha)
            {
                return (null, "no CI commit variable is set and this folder is not a git repository");
            }

            // Tracked files only: deployify-state.json and generated output are often untracked.
            var status = await runner.RunAsync(new ProcessSpec("git", ["status", "--porcelain", "--untracked-files=no"])
            {
                WorkingDirectory = directory, Timeout = s_gitTimeout
            }, ct);
            if (!status.Ok)
            {
                return (null, "git could not report whether the working tree has uncommitted changes");
            }

            if (!string.IsNullOrWhiteSpace(status.Stdout))
            {
                return (null, "the working tree has uncommitted changes, so the commit SHA doesn't identify this build");
            }

            return (new CommitTag(sha, "git"), null);
        }
        catch (Exception ex) when (ex is ToolNotFoundException or TimeoutException)
        {
            return (null, "no CI commit variable is set and git is not available");
        }
    }

    /// <summary>A commit SHA (7 to 64 hex characters) in lowercase, or null.</summary>
    internal static string? Normalize(string? value)
    {
        var sha = value?.Trim().ToLowerInvariant();
        return sha is { Length: >= 7 and <= 64 } && sha.All(char.IsAsciiHexDigitLower) ? sha : null;
    }
}
