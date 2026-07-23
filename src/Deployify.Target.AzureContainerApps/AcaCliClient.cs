using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AzureContainerApps;

/// <summary>
/// Thin wrapper around the Azure CLI for Container Apps operations.
/// Requires `az` to be installed and logged in (`az login` or service principal env vars).
/// </summary>
public sealed class AcaCliClient(AcaTargetConfig config, ILogger logger)
{
    // ── Availability ──────────────────────────────────────────────────────────

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (exit, _) = await RunAsync("az", "--version");
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var args = BuildArgs(
            $"containerapp env show --name {config.EnvironmentName} " +
            $"--resource-group {config.ResourceGroup} --output json");

        var (exit, output) = await RunAsync("az", args, ct);
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
        var args = BuildArgs(
            $"containerapp list --resource-group {config.ResourceGroup} --output json");

        var (exit, output) = await RunAsync("az", args, ct);
        if (exit != 0)
        {
            logger.LogWarning("az containerapp list failed: {Output}", output);
            return [];
        }

        try
        {
            var apps = JsonDocument.Parse(output).RootElement;
            return apps.EnumerateArray()
                .Select(a => a.GetProperty("name").GetString()!)
                .ToList();
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

        string args;
        if (running.Contains(appName))
        {
            // Update via ARM template / YAML
            args = BuildArgs(
                $"containerapp update --name {appName} " +
                $"--resource-group {config.ResourceGroup} " +
                $"--yaml \"{appSpecPath}\" --output json");
        }
        else
        {
            args = BuildArgs(
                $"containerapp create --name {appName} " +
                $"--resource-group {config.ResourceGroup} " +
                $"--environment {config.EnvironmentName} " +
                $"--yaml \"{appSpecPath}\" --output json");
        }

        var (exit, output) = await RunAsync("az", args, ct);
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
        var args = BuildArgs(
            $"containerapp revision list --name {appName} " +
            $"--resource-group {config.ResourceGroup} " +
            $"--output json");

        var (exit, output) = await RunAsync("az", args, ct);
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private string BuildArgs(string command)
    {
        var sub = config.SubscriptionId is not null
            ? $"--subscription {config.SubscriptionId} "
            : string.Empty;
        var tenant = config.TenantId is not null
            ? $"--tenant {config.TenantId} "
            : string.Empty;
        return $"{sub}{tenant}{command}";
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string command, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {command}.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        var output = stdoutTask.Result;
        var stderr = stderrTask.Result;
        return (proc.ExitCode, string.IsNullOrWhiteSpace(output) ? stderr : output);
    }
}
