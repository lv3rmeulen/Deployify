using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// Thin wrapper around the Azure CLI for Container Apps operations.
/// Requires `az` to be installed and logged in (`az login` or service principal env vars).
/// </summary>
public sealed class AcaCliClient(AcaTargetConfig config, ILogger logger, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    // ── Availability ──────────────────────────────────────────────────────────

    public static Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => IsAvailableAsync(ProcessRunner.Default, ct);

    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("az", ["--version"], ct: ct);

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "env", "show", "--name", config.EnvironmentName,
            "--resource-group", config.ResourceGroup, "--output", "json"), ct);
        if (exit == 0)
        {
            return (true, null);
        }

        // Distinguish "not found" (env doesn't exist yet — still ok) from auth failure
        if (output.Contains("ResourceNotFound") || output.Contains("not found"))
        {
            return (true, null); // environment will be created on first apply
        }

        return (false, $"Cannot reach Azure Container Apps: {output.Trim()}");
    }

    // ── Running apps ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetRunningAppNamesAsync(CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "list", "--resource-group", config.ResourceGroup, "--output", "json"), ct);
        if (exit != 0)
        {
            logger.LogWarning("az containerapp list failed: {Output}", output);
            return [];
        }

        try
        {
            var apps = JsonDocument.Parse(output).RootElement;
            return
            [
                .. apps.EnumerateArray()
                    .Select(a => a.GetProperty("name").GetString()!)
            ];
        }
        catch
        {
            return [];
        }
    }

    // ── Create / update app ───────────────────────────────────────────────────

    public async Task<string> DeployAppAsync(
        string appSpecPath, string appName, CancellationToken ct = default)
    {
        var running = await GetRunningAppNamesAsync(ct);

        var args = running.Contains(appName)
            // Update via ARM template / YAML
            ? BuildArgs("containerapp", "update", "--name", appName, "--resource-group", config.ResourceGroup,
                "--yaml", appSpecPath, "--output", "json")
            : BuildArgs("containerapp", "create", "--name", appName, "--resource-group", config.ResourceGroup,
                "--environment", config.EnvironmentName, "--yaml", appSpecPath, "--output", "json");

        var (exit, output) = await RunAsync(args, ct);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to deploy Container App '{appName}': {output.Trim()}");
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            return doc.RootElement.GetProperty("id").GetString()!;
        }
        catch
        {
            return $"{config.ResourceGroup}/{appName}";
        }
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ValidateAppSpecAsync(
        string specPath, CancellationToken ct = default)
    {
        var errors = new List<string>();
        try
        {
            var json = await File.ReadAllTextAsync(specPath, ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("name", out _))
            {
                errors.Add("Missing required field: name");
            }

            if (!root.TryGetProperty("location", out _))
            {
                errors.Add("Missing required field: location");
            }

            if (!root.TryGetProperty("properties", out var props))
            {
                errors.Add("Missing required field: properties");
            }
            else
            {
                if (!props.TryGetProperty("environmentId", out _))
                {
                    errors.Add("Missing required field: properties.environmentId");
                }

                if (!props.TryGetProperty("template", out var tmpl))
                {
                    errors.Add("Missing required field: properties.template");
                }
                else if (!tmpl.TryGetProperty("containers", out _))
                {
                    errors.Add("Missing required field: properties.template.containers");
                }
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return errors;
    }

    // ── App revision info ─────────────────────────────────────────────────────

    public async Task<string?> GetLatestRevisionAsync(
        string appName, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "revision", "list", "--name", appName,
            "--resource-group", config.ResourceGroup, "--output", "json"), ct);
        if (exit != 0)
        {
            return null;
        }

        try
        {
            var revisions = JsonDocument.Parse(output).RootElement;
            return revisions.EnumerateArray()
                .OrderByDescending(r => r.GetProperty("properties")
                    .GetProperty("createdTime").GetString())
                .Select(r => r.GetProperty("name").GetString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Restarts a revision, so it reads Key Vault secrets again.</summary>
    public async Task<bool> RestartRevisionAsync(string appName, string revision, CancellationToken ct = default)
    {
        var (exit, _) = await RunAsync(BuildArgs(
            "containerapp", "revision", "restart", "--name", appName,
            "--resource-group", config.ResourceGroup, "--revision", revision), ct);
        return exit == 0;
    }

    // ── Rollout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's latest and latest-ready revision, or null when the app doesn't exist. Throws
    /// when Azure can't be asked, so "unreachable" is never taken for "new app".
    /// </summary>
    public async Task<AcaAppStatus?> GetAppAsync(string appName, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "show", "--name", appName, "--resource-group", config.ResourceGroup, "--output", "json"), ct);
        if (exit != 0)
        {
            return IsNotFound(output)
                ? null
                : throw new InvalidOperationException($"Failed to read Container App '{appName}': {output.Trim()}");
        }

        return AcaAppStatus.Parse(output);
    }

    /// <summary>One revision's state, or null when it doesn't exist (yet).</summary>
    public async Task<AcaRevisionStatus?> GetRevisionAsync(string appName, string revision, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "revision", "show", "--name", appName, "--resource-group", config.ResourceGroup,
            "--revision", revision, "--output", "json"), ct);
        if (exit != 0)
        {
            return IsNotFound(output)
                ? null
                : throw new InvalidOperationException($"Failed to read revision '{revision}' of '{appName}': {output.Trim()}");
        }

        return AcaRevisionStatus.Parse(output);
    }

    /// <summary>
    /// Creates a new revision from an earlier one's template (<c>az containerapp revision copy</c>),
    /// which is how an app goes back to what it ran before.
    /// </summary>
    public async Task CopyRevisionAsync(string appName, string fromRevision, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "revision", "copy", "--name", appName, "--resource-group", config.ResourceGroup,
            "--from-revision", fromRevision, "--output", "json"), ct);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Failed to copy revision '{fromRevision}' of '{appName}': {output.Trim()}");
        }
    }

    public async Task DeleteAppAsync(string appName, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "containerapp", "delete", "--name", appName, "--resource-group", config.ResourceGroup, "--yes"), ct);
        if (exit != 0 && !IsNotFound(output))
        {
            throw new InvalidOperationException($"Failed to delete Container App '{appName}': {output.Trim()}");
        }
    }

    private static bool IsNotFound(string output)
        => output.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("was not found", StringComparison.OrdinalIgnoreCase) ||
           output.Contains("could not be found", StringComparison.OrdinalIgnoreCase);

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Prefixes the subscription when one is configured. (An empty value used to produce
    /// a bare "--subscription" with no argument. "--tenant" isn't accepted by containerapp
    /// commands; the tenant comes from `az login`.)
    /// </summary>
    internal IReadOnlyList<string> BuildArgs(params string[] command)
        => string.IsNullOrWhiteSpace(config.SubscriptionId)
            ? command
            : ["--subscription", config.SubscriptionId, .. command];

    /// <summary>The subscription the Azure CLI is logged in to, or null.</summary>
    public async Task<string?> GetCurrentSubscriptionAsync(CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(["account", "show", "--query", "id", "--output", "tsv"], ct);
        var id = output.Trim();
        return exit == 0 && id.Length > 0 && !id.Contains(' ') ? id : null;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await _runner.RunAsync(new ProcessSpec("az", args) { Log = logger }, ct);
        return (result.ExitCode, result.Output);
    }
}
