using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.CloudRun;

/// <summary>
/// Talks to Cloud Run and Secret Manager through the gcloud CLI, signed in the way gcloud is
/// (a user, a service account, or workload identity federation in CI). Every call names the
/// project (and region for Cloud Run) explicitly, so gcloud's own defaults never decide.
/// </summary>
public sealed class GcloudClient(CloudRunTargetConfig config, ILogger logger, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("gcloud", ["--version"], TimeSpan.FromSeconds(30), ct);

    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ProjectId))
        {
            return (false, "cloudrun.projectId is not set.");
        }

        var result = await RunAsync(ct, ["run", "services", "list", .. Scope(), "--limit", "1", "--format", "json"]);
        return result.Ok ? (true, null) : (false, result.Stderr.Trim());
    }

    /// <summary>The project's number, from <c>gcloud projects describe</c>; null when it can't be read.</summary>
    public async Task<string?> GetProjectNumberAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "projects", "describe", config.ProjectId, "--format", "value(projectNumber)");
        var number = result.Stdout.Trim();
        return result.Ok && number.Length > 0 && number.All(char.IsAsciiDigit) ? number : null;
    }

    // ── Services ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or replaces a service from a spec file. With <paramref name="wait"/> off gcloud
    /// returns once Cloud Run has the change (the health gate then watches); on, it waits until
    /// the new revision serves or fails. Returns the service as Cloud Run then reports it.
    /// </summary>
    public async Task<CloudRunServiceStatus> ReplaceAsync(string service, string specPath, bool wait, CancellationToken ct = default)
    {
        string[] asyncFlag = wait ? [] : ["--async"];
        var result = await RunAsync(ct, ["run", "services", "replace", specPath, .. Scope(), .. asyncFlag, "--format", "json"]);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"gcloud run services replace failed: {result.Stderr.Trim()}");
        }

        // gcloud prints the service with --format json; with --async it may print less, so read it back then.
        return (TryParse(result.Stdout) is { Generation: > 0 } replaced ? replaced : null)
               ?? await DescribeAsync(service, ct)
            ?? throw new InvalidOperationException("Cloud Run reported no service after the replace.");
    }

    /// <summary>The service, or null when it doesn't exist. Throws when Cloud Run can't be asked.</summary>
    public async Task<CloudRunServiceStatus?> DescribeAsync(string service, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "services", "describe", service, .. Scope(), "--format", "json"]);
        if (!result.Ok)
        {
            return IsNotFound(result.Stderr)
                ? null
                : throw new InvalidOperationException($"gcloud could not read Cloud Run service {service}: {result.Stderr.Trim()}");
        }

        return CloudRunServiceStatus.Parse(result.Stdout);
    }

    /// <summary>One revision, or null when it doesn't exist (yet).</summary>
    public async Task<CloudRunRevisionStatus?> DescribeRevisionAsync(string revision, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "revisions", "describe", revision, .. Scope(), "--format", "json"]);
        if (!result.Ok)
        {
            return IsNotFound(result.Stderr)
                ? null
                : throw new InvalidOperationException($"gcloud could not read revision {revision}: {result.Stderr.Trim()}");
        }

        return CloudRunRevisionStatus.Parse(result.Stdout);
    }

    /// <summary>Sets the traffic split by revision name (<c>update-traffic --to-revisions</c>).</summary>
    public async Task SetTrafficAsync(string service, IReadOnlyDictionary<string, int> percents, CancellationToken ct = default)
    {
        var split = string.Join(',', percents.Select(p => $"{p.Key}={p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var result = await RunAsync(ct, ["run", "services", "update-traffic", service, .. Scope(), "--to-revisions", split]);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"gcloud could not set the traffic of {service}: {result.Stderr.Trim()}");
        }
    }

    /// <summary>Removes a traffic tag; a tag that isn't there is not an error.</summary>
    public async Task RemoveTagAsync(string service, string tag, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "services", "update-traffic", service, .. Scope(), "--remove-tags", tag]);
        if (!result.Ok && !result.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"gcloud could not remove tag {tag} of {service}: {result.Stderr.Trim()}");
        }
    }

    /// <summary>Lets anyone who can reach the service call it (<c>allUsers</c> as Cloud Run Invoker).</summary>
    public async Task<bool> AllowUnauthenticatedAsync(string service, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "services", "add-iam-policy-binding", service, .. Scope(),
            "--member", "allUsers", "--role", "roles/run.invoker", "--format", "none"]);
        if (!result.Ok)
        {
            logger.LogWarning("Could not let unauthenticated callers reach {Service} ({Message}). An organization policy may forbid " +
                              "allUsers; callers then need an identity token, or set cloudrun.allowUnauthenticated to false.",
                service, result.Stderr.Trim());
        }

        return result.Ok;
    }

    /// <summary>Services in the project and region that carry Deployify's label. Throws when Cloud Run can't be asked.</summary>
    public async Task<IReadOnlyList<CloudRunServiceStatus>> ListManagedAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "services", "list", .. Scope(),
            "--filter", $"metadata.labels.{CloudRunManifestGenerator.ManagedByLabel}={CloudRunManifestGenerator.ManagedByValue}",
            "--format", "json"]);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"gcloud could not list Cloud Run services: {result.Stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Stdout) ? "[]" : result.Stdout);
        return [.. doc.RootElement.EnumerateArray().Select(e => CloudRunServiceStatus.Parse(e.GetRawText()))];
    }

    public async Task DeleteServiceAsync(string service, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, ["run", "services", "delete", service, .. Scope()]);
        if (!result.Ok && !IsNotFound(result.Stderr))
        {
            throw new InvalidOperationException($"gcloud could not delete Cloud Run service {service}: {result.Stderr.Trim()}");
        }
    }

    // ── Logs ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Log entries matching <paramref name="filter"/>, newest first (<c>gcloud logging read</c>).
    /// Cloud Logging only looks back a day unless <paramref name="longAgo"/> widens that to 30 days.
    /// </summary>
    public async Task<string> ReadLogsAsync(string filter, int limit, bool longAgo, CancellationToken ct = default)
    {
        string[] freshness = longAgo ? ["--freshness", "30d"] : [];
        var result = await RunAsync(ct, ["logging", "read", filter, "--project", config.ProjectId,
            "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--order", "desc", .. freshness, "--format", "json"]);
        if (!result.Ok)
        {
            throw new Deployify.Core.ServiceLogs.ServiceLogsException($"gcloud could not read the logs: {result.Stderr.Trim()}");
        }

        return result.Stdout;
    }

    // ── Secret Manager ────────────────────────────────────────────────────────

    /// <summary>The latest value of a secret, or null when the secret doesn't exist. The value stays in memory.</summary>
    public async Task<string?> AccessLatestAsync(string project, string secret, CancellationToken ct = default)
    {
        var result = await RunSecretAsync(null, ct, "secrets", "versions", "access", "latest", "--secret", secret, "--project", project);
        if (!result.Ok)
        {
            return IsNotFound(result.Stderr)
                ? null
                : throw new InvalidOperationException($"gcloud could not read secret {secret}: {result.Stderr.Trim()}");
        }

        return result.Stdout;
    }

    /// <summary>The number of the latest version of a secret.</summary>
    public async Task<string> LatestVersionAsync(string project, string secret, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "secrets", "versions", "describe", "latest", "--secret", secret, "--project", project,
            "--format", "value(name)");
        return result.Ok
            ? VersionOf(result.Stdout)
            : throw new InvalidOperationException($"gcloud could not read the versions of secret {secret}: {result.Stderr.Trim()}");
    }

    /// <summary>
    /// Writes <paramref name="value"/> as a new version of <paramref name="secret"/>, creating the
    /// secret first when it doesn't exist. Returns the version number.
    /// </summary>
    public async Task<string> WriteAsync(
        string project, string secret, string value, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
    {
        var exists = (await RunAsync(ct, "secrets", "describe", secret, "--project", project, "--format", "value(name)")).Ok;
        if (!exists)
        {
            var created = await RunSecretAsync(null, ct, "secrets", "create", secret, "--project", project,
                "--replication-policy", "automatic", "--labels", string.Join(',', labels.Select(l => $"{l.Key}={l.Value}")));
            if (!created.Ok && !created.Stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"gcloud could not create secret {secret}: {created.Stderr.Trim()}");
            }
        }

        var added = await RunSecretAsync(value, ct, "secrets", "versions", "add", secret, "--project", project,
            "--data-file", "-", "--format", "value(name)");
        return added.Ok
            ? VersionOf(added.Stdout)
            : throw new InvalidOperationException($"gcloud could not write secret {secret}: {added.Stderr.Trim()}");
    }

    /// <summary>Secrets whose labels match <paramref name="labelFilter"/> (<c>key=value</c>, optionally <c>AND labels.key=value</c>).</summary>
    public async Task<IReadOnlyList<string>> ListSecretsAsync(string project, string labelFilter, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "secrets", "list", "--project", project, "--filter", $"labels.{labelFilter}", "--format", "value(name)");
        if (!result.Ok)
        {
            throw new InvalidOperationException($"gcloud could not list secrets: {result.Stderr.Trim()}");
        }

        return [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n[(n.LastIndexOf('/') + 1)..])];
    }

    public async Task DeleteSecretAsync(string project, string secret, CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "secrets", "delete", secret, "--project", project);
        if (!result.Ok && !IsNotFound(result.Stderr))
        {
            throw new InvalidOperationException($"gcloud could not delete secret {secret}: {result.Stderr.Trim()}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string[] Scope() => ["--project", config.ProjectId, "--region", config.Region];

    /// <summary><c>projects/123/secrets/x/versions/4</c> → <c>4</c>.</summary>
    internal static string VersionOf(string name)
    {
        var trimmed = name.Trim();
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    internal static bool IsNotFound(string stderr)
        => stderr.Contains("NOT_FOUND", StringComparison.Ordinal) ||
           stderr.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
           stderr.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static CloudRunServiceStatus? TryParse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : CloudRunServiceStatus.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args)
        => _runner.RunAsync(new ProcessSpec("gcloud", [.. args, "--quiet"]) { Log = logger, LogTag = "gcloud" }, ct);

    /// <summary>For calls that read or write secret values: nothing is logged.</summary>
    private Task<ProcessResult> RunSecretAsync(string? stdin, CancellationToken ct, params string[] args)
        => _runner.RunAsync(new ProcessSpec("gcloud", [.. args, "--quiet"]) { Stdin = stdin, Sensitive = true }, ct);
}
