using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.GitOps;

/// <summary>
/// Wraps the git CLI for Deployify's own clone of the config repository. Credentials come from
/// the user's git setup (credential helper, SSH agent); git is never allowed to prompt.
/// </summary>
public sealed class GitRunner(string checkoutDirectory, ILogger logger, IProcessRunner? runner = null)
{
    private static readonly IReadOnlyDictionary<string, string?> s_env = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never"
    };

    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    public string CheckoutDirectory { get; } = checkoutDirectory;

    /// <summary>True when git is on PATH.</summary>
    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("git", ["--version"], ct: ct);

    /// <summary>Checks that the repository can be read with the user's credentials.</summary>
    public async Task<(bool Ok, string? Error)> CanReadAsync(string repoUrl, CancellationToken ct = default)
    {
        var result = await RunAsync(["ls-remote", "--heads", repoUrl], ct, inCheckout: false);
        return result.Ok ? (true, null) : (false, $"git can't read {repoUrl}: {result.ErrorOrOutput.Trim()}");
    }

    /// <summary>
    /// Makes the clone match the remote branch exactly, cloning it first when needed. Local
    /// changes are discarded. When the branch doesn't exist yet the working tree is emptied and
    /// the first commit starts it. Returns whether the branch exists on the remote.
    /// </summary>
    public async Task<bool> SyncAsync(string repoUrl, string branch, CancellationToken ct = default)
    {
        if (!Directory.Exists(System.IO.Path.Combine(CheckoutDirectory, ".git")))
        {
            Directory.CreateDirectory(CheckoutDirectory);
            (await RunAsync(["clone", "--no-checkout", repoUrl, CheckoutDirectory], ct, inCheckout: false))
                .EnsureSuccess($"git clone {repoUrl}");
        }
        else
        {
            (await RunAsync(["remote", "set-url", "origin", repoUrl], ct)).EnsureSuccess("git remote set-url");
        }

        (await RunAsync(["fetch", "--prune", "origin"], ct)).EnsureSuccess($"git fetch {repoUrl}");

        var heads = await RunAsync(["ls-remote", "--heads", "origin", branch], ct);
        heads.EnsureSuccess($"git ls-remote {repoUrl}");
        var exists = !string.IsNullOrWhiteSpace(heads.Stdout);
        if (exists)
        {
            (await RunAsync(["checkout", "--force", "-B", branch, $"origin/{branch}"], ct)).EnsureSuccess($"git checkout {branch}");
            (await RunAsync(["reset", "--hard", $"origin/{branch}"], ct)).EnsureSuccess("git reset");
        }
        else
        {
            logger.LogInformation("Branch {Branch} doesn't exist in {Repo} yet; the first commit creates it.", branch, repoUrl);
            (await RunAsync(["checkout", "--force", "--orphan", branch], ct)).EnsureSuccess($"git checkout --orphan {branch}");
            (await RunAsync(["rm", "-r", "-q", "--cached", "--ignore-unmatch", "."], ct)).EnsureSuccess("git rm");
        }

        (await RunAsync(["clean", "-f", "-d", "-x"], ct)).EnsureSuccess("git clean");
        return exists;
    }

    /// <summary>
    /// Stages everything under <paramref name="path"/> and commits it. Returns the new commit,
    /// or null when nothing changed.
    /// </summary>
    public async Task<string?> CommitAsync(string path, string message, string authorName, string authorEmail, CancellationToken ct = default)
    {
        (await RunAsync(["add", "--all", "--", path], ct)).EnsureSuccess("git add");
        var staged = await RunAsync(["diff", "--cached", "--quiet", "--", path], ct);
        if (staged.Ok)
        {
            return null;
        }

        (await RunAsync(
        [
            "-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}",
            "commit", "--quiet", "-m", message, "--", path
        ], ct)).EnsureSuccess("git commit");
        return await HeadAsync(ct);
    }

    /// <summary>The commit checked out, or null on a branch without commits.</summary>
    public async Task<string?> HeadAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], ct);
        return result.Ok && !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout.Trim() : null;
    }

    /// <summary>Pushes the current commit to <paramref name="branch"/>. Throws when the push is rejected.</summary>
    public async Task PushAsync(string branch, CancellationToken ct = default)
    {
        var result = await RunAsync(["push", "--porcelain", "origin", $"HEAD:refs/heads/{branch}"], ct);
        if (result.Ok)
        {
            return;
        }

        var error = result.ErrorOrOutput.Trim();
        if (error.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Someone else pushed to {branch} while Deployify was working. Run apply again. ({error})");
        }

        throw new InvalidOperationException($"git push to {branch} failed: {error}");
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool inCheckout = true)
        => _runner.RunAsync(new ProcessSpec("git", inCheckout ? ["-C", CheckoutDirectory, .. args] : args)
        {
            Env = s_env,
            Log = logger,
            LogTag = "git"
        }, ct);
}
