using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// Thin wrapper around the AWS CLI for ECS operations.
/// All operations require the AWS CLI to be installed and configured.
///
/// Uses the CLI rather than the SDK to avoid a heavy dependency and to
/// keep credentials management simple (honours all standard AWS credential
/// sources: env vars, ~/.aws/credentials, instance profiles, etc.).
/// </summary>
public sealed class EcsCliClient(EcsTargetConfig config, ILogger logger)
{
    // ── Availability ──────────────────────────────────────────────────────────

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var (exit, _) = await RunAsync("aws", "--version");
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
        var args = BuildArgs($"ecs describe-clusters --clusters {config.ClusterName} --output json");
        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
	        return (false, $"Cannot reach AWS ECS: {output.Trim()}");
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            var clusters = doc.RootElement.GetProperty("clusters");
            if (clusters.GetArrayLength() == 0)
            {
	            return (false, $"ECS cluster '{config.ClusterName}' not found in {config.Region}.");
            }

            return (true, null);
        }
        catch
        {
            return (false, $"Unexpected response from AWS CLI: {output.Trim()}");
        }
    }

    // ── Running services ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetRunningServiceNamesAsync(CancellationToken ct = default)
    {
        var args = BuildArgs($"ecs list-services --cluster {config.ClusterName} --output json");
        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
            logger.LogWarning("ecs list-services failed: {Output}", output);
            return [];
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            return doc.RootElement
                .GetProperty("serviceArns")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .Select(arn => arn.Split('/').Last()) // ARN → service name
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ── Register task definition ──────────────────────────────────────────────

    public async Task<string> RegisterTaskDefinitionAsync(
        string taskDefJsonPath, CancellationToken ct = default)
    {
        var args = BuildArgs(
            $"ecs register-task-definition --cli-input-json file://{taskDefJsonPath} --output json");
        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
	        throw new InvalidOperationException(
		        $"Failed to register task definition from {taskDefJsonPath}: {output.Trim()}");
        }

        var doc = JsonDocument.Parse(output);
        return doc.RootElement
            .GetProperty("taskDefinition")
            .GetProperty("taskDefinitionArn")
            .GetString()!;
    }

    // ── Create or update service ──────────────────────────────────────────────

    public async Task<string> UpsertServiceAsync(
        string serviceName,
        string taskDefinitionArn,
        int desiredCount,
        CancellationToken ct = default)
    {
        var running = await GetRunningServiceNamesAsync(ct);
        if (running.Contains(serviceName))
        {
	        return await UpdateServiceAsync(serviceName, taskDefinitionArn, desiredCount, ct);
        }

        return await CreateServiceAsync(serviceName, taskDefinitionArn, desiredCount, ct);
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> ValidateTaskDefinitionAsync(
        string taskDefJsonPath, CancellationToken ct = default)
    {
        // Parse JSON locally — basic structural validation without calling AWS
        var errors = new List<string>();
        try
        {
            var json = await File.ReadAllTextAsync(taskDefJsonPath, ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("family", out _))
            {
	            errors.Add("Missing required field: family");
            }

            if (!root.TryGetProperty("containerDefinitions", out var containers))
            {
	            errors.Add("Missing required field: containerDefinitions");
            }
            else if (containers.GetArrayLength() == 0)
            {
	            errors.Add("containerDefinitions must have at least one entry");
            }

            if (!root.TryGetProperty("networkMode", out _))
            {
	            errors.Add("Missing required field: networkMode");
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return errors;
    }

    // ── Describe running service ──────────────────────────────────────────────

    public async Task<string?> GetRunningTaskDefinitionArnAsync(
        string serviceName, CancellationToken ct = default)
    {
        var args = BuildArgs(
            $"ecs describe-services --cluster {config.ClusterName} --services {serviceName} --output json");
        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
	        return null;
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            var services = doc.RootElement.GetProperty("services");
            if (services.GetArrayLength() == 0)
            {
	            return null;
            }

            return services[0].GetProperty("taskDefinition").GetString();
        }
        catch
        {
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> CreateServiceAsync(
        string serviceName, string taskDefinitionArn, int desiredCount, CancellationToken ct)
    {
        var networkConfig = BuildNetworkConfig();
        var launchType = config.LaunchType.ToString().ToUpperInvariant();

        var args = BuildArgs(
            $"ecs create-service " +
            $"--cluster {config.ClusterName} " +
            $"--service-name {serviceName} " +
            $"--task-definition {taskDefinitionArn} " +
            $"--desired-count {desiredCount} " +
            $"--launch-type {launchType} " +
            $"--network-configuration \"{networkConfig}\" " +
            $"--output json");

        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
	        throw new InvalidOperationException($"Failed to create ECS service '{serviceName}': {output.Trim()}");
        }

        var doc = JsonDocument.Parse(output);
        return doc.RootElement.GetProperty("service").GetProperty("serviceArn").GetString()!;
    }

    private async Task<string> UpdateServiceAsync(
        string serviceName, string taskDefinitionArn, int desiredCount, CancellationToken ct)
    {
        var args = BuildArgs(
            $"ecs update-service " +
            $"--cluster {config.ClusterName} " +
            $"--service {serviceName} " +
            $"--task-definition {taskDefinitionArn} " +
            $"--desired-count {desiredCount} " +
            $"--output json");

        var (exit, output) = await RunAsync("aws", args, ct);
        if (exit != 0)
        {
	        throw new InvalidOperationException($"Failed to update ECS service '{serviceName}': {output.Trim()}");
        }

        var doc = JsonDocument.Parse(output);
        return doc.RootElement.GetProperty("service").GetProperty("serviceArn").GetString()!;
    }

    private string BuildNetworkConfig()
    {
        var subnets = string.Join(",", config.SubnetIds);
        var sgs = string.Join(",", config.SecurityGroupIds);
        return $"awsvpcConfiguration={{subnets=[{subnets}],securityGroups=[{sgs}],assignPublicIp=ENABLED}}";
    }

    private string BuildArgs(string command) =>
        config.AwsProfile is not null
            ? $"--profile {config.AwsProfile} --region {config.Region} {command}"
            : $"--region {config.Region} {command}";

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
