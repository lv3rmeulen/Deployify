using System;

namespace Deployify.Core.Services;

/// <summary>Who is running a release, and the CI run it belongs to, for the release record.</summary>
/// <param name="Actor">The CI user name, or the local account name outside CI.</param>
/// <param name="ActorSource">The variable <paramref name="Actor"/> came from, or <c>local</c>.</param>
/// <param name="RunUrl">A link to the CI run, or null outside CI.</param>
public sealed record ReleaseContext(string? Actor, string? ActorSource, string? RunUrl)
{
    /// <summary>CI variables that name the user who started the run, in the order they are checked.</summary>
    private static readonly string[] s_actorVariables =
    [
        "DEPLOYIFY_ACTOR",    // set by hand, for CI systems not listed here
        "GITHUB_ACTOR",       // GitHub Actions
        "BUILD_REQUESTEDFOR", // Azure Pipelines
        "GITLAB_USER_LOGIN",  // GitLab CI
        "BUILD_USER_ID"       // Jenkins (build-user-vars plugin)
    ];

    /// <summary>
    /// Reads the actor and run link from <c>DEPLOYIFY_ACTOR</c> and <c>DEPLOYIFY_RUN_URL</c>, or
    /// the CI variables of GitHub Actions, Azure Pipelines, GitLab CI and Jenkins. Outside CI the actor is <paramref name="localUser"/> (by default the
    /// OS account name) and there is no run link.
    /// </summary>
    public static ReleaseContext Resolve(Func<string, string?>? getEnvironmentVariable = null, string? localUser = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        string? Get(string name) => getEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

        string? actor = null, source = null;
        foreach (var name in s_actorVariables)
        {
            if (Get(name) is { } value)
            {
                (actor, source) = (value, name);
                break;
            }
        }

        var runUrl = DetectRunUrl(getEnvironmentVariable);
        if (actor is null && runUrl is null)
        {
            (actor, source) = (localUser ?? Environment.UserName, "local");
        }

        return new ReleaseContext(actor, source, runUrl);
    }

    /// <summary>
    /// A link to the CI run this process belongs to: <c>DEPLOYIFY_RUN_URL</c>, or one built from
    /// GitHub Actions, GitLab CI, Azure Pipelines or Jenkins variables. Null outside CI.
    /// </summary>
    public static string? DetectRunUrl(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        string? get(string name) => getEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

        if (get("DEPLOYIFY_RUN_URL") is { } custom)
        {
            return custom;
        }

        if (get("GITHUB_RUN_ID") is { } runId && get("GITHUB_REPOSITORY") is { } repo)
        {
            return $"{(get("GITHUB_SERVER_URL") ?? "https://github.com").TrimEnd('/')}/{repo}/actions/runs/{runId}";
        }

        if (get("CI_JOB_URL") is { } gitlab)
        {
            return gitlab;
        }

        if (get("SYSTEM_COLLECTIONURI") is { } collection && get("SYSTEM_TEAMPROJECT") is { } project &&
            get("BUILD_BUILDID") is { } buildId)
        {
            return $"{collection.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId}";
        }

        return get("BUILD_URL"); // Jenkins
    }
}
