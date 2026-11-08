using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Processes;
using Deployify.Core.SecretBackends;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.AwsEcs;

/// <summary>
/// Thin wrapper around the AWS CLI for ECS operations.
/// All operations require the AWS CLI to be installed and configured.
///
/// Uses the CLI rather than the SDK to avoid a heavy dependency and to
/// keep credentials management simple (honors all standard AWS credential
/// sources: env vars, ~/.aws/credentials, instance profiles, etc.).
/// </summary>
public sealed class EcsCliClient(EcsTargetConfig config, ILogger logger, IProcessRunner? runner = null)
    : ISsmParameterClient
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Default;

    // ── Availability ──────────────────────────────────────────────────────────

    public static Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => IsAvailableAsync(ProcessRunner.Default, ct);

    public static Task<bool> IsAvailableAsync(IProcessRunner runner, CancellationToken ct = default)
        => runner.IsAvailableAsync("aws", ["--version"], ct: ct);

    // ── Ping ──────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs("ecs", "describe-clusters", "--clusters", config.ClusterName, "--output", "json"), ct);
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
        var (exit, output) = await RunAsync(BuildArgs("ecs", "list-services", "--cluster", config.ClusterName, "--output", "json"), ct);
        if (exit != 0)
        {
            logger.LogWarning("ecs list-services failed: {Output}", output);
            return [];
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            return
            [
                .. doc.RootElement
                    .GetProperty("serviceArns")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .Select(arn => arn.Split('/').Last()) // ARN → service name
            ];
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
        var (exit, output) = await RunAsync(BuildArgs(
            "ecs", "register-task-definition", "--cli-input-json", $"file://{taskDefJsonPath}", "--output", "json"), ct);
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

    // ── Secrets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or overwrites a SecureString parameter in SSM Parameter Store. The value goes
    /// through a temporary owner-only input file rather than the command line, so it doesn't
    /// show up in the process list.
    /// </summary>
    public async Task<long> PutSecureStringParameterAsync(string name, string value, CancellationToken ct = default)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"deployify-ssm-{Guid.NewGuid():N}.json");
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Name"] = name,
                ["Value"] = value,
                ["Type"] = "SecureString",
                ["Overwrite"] = true
            });
            await File.WriteAllTextAsync(inputPath, json, ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(inputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var (exit, output) = await RunAsync(BuildArgs(
                "ssm", "put-parameter", "--cli-input-json", $"file://{inputPath}", "--output", "json"), ct);
            if (exit != 0)
            {
                throw new InvalidOperationException($"Failed to store secret parameter {name}: {output.Trim()}");
            }

            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("Version", out var v) ? v.GetInt64() : 0;
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    /// <inheritdoc />
    public async Task<SsmParameter?> GetParameterAsync(string name, CancellationToken ct = default)
    {
        // Output holds the decrypted value: it's parsed and wrapped, never logged.
        var (exit, output) = await RunAsync(
            BuildArgs("ssm", "get-parameter", "--name", name, "--with-decryption", "--output", "json"), ct, sensitive: true);
        if (exit != 0)
        {
            if (output.Contains("ParameterNotFound", StringComparison.Ordinal))
            {
                return null;
            }

            throw new InvalidOperationException($"Failed to read secret parameter {name}: {output.Trim()}");
        }

        using var doc = JsonDocument.Parse(output);
        return ReadParameter(doc.RootElement.GetProperty("Parameter"));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, SsmParameter>> GetParametersByPathAsync(
        string path, CancellationToken ct = default)
    {
        // The CLI follows NextToken itself and returns every page.
        var (exit, output) = await RunAsync(
            BuildArgs("ssm", "get-parameters-by-path", "--path", path, "--recursive", "--with-decryption", "--output", "json"),
            ct, sensitive: true);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to read secret parameters under {path}: {output.Trim()}");
        }

        return ParseParameters(output);
    }

    /// <summary>Parses <c>get-parameters-by-path</c> output.</summary>
    internal static IReadOnlyDictionary<string, SsmParameter> ParseParameters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, SsmParameter>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.GetProperty("Parameters").EnumerateArray())
        {
            var parameter = ReadParameter(p);
            result[parameter.Name] = parameter;
        }

        return result;
    }

    private static SsmParameter ReadParameter(JsonElement p) => new(
        p.GetProperty("Name").GetString()!,
        new SensitiveValue(p.GetProperty("Value").GetString() ?? ""),
        p.TryGetProperty("Version", out var v) ? v.GetInt64() : 0);

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
        var (exit, output) = await RunAsync(BuildArgs(
            "ecs", "describe-services", "--cluster", config.ClusterName, "--services", serviceName, "--output", "json"), ct);
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
        [
            "ecs", "create-service",
            "--cluster", config.ClusterName,
            "--service-name", serviceName,
            "--task-definition", taskDefinitionArn,
            "--desired-count", desiredCount.ToString(CultureInfo.InvariantCulture),
            "--launch-type", launchType,
            "--network-configuration", networkConfig,
            .. await ServiceConnectArgsAsync(serviceName, ct),
            "--output", "json"
        ]);

        var (exit, output) = await RunAsync(args, ct);
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
        [
            "ecs", "update-service",
            "--cluster", config.ClusterName,
            "--service", serviceName,
            "--task-definition", taskDefinitionArn,
            "--desired-count", desiredCount.ToString(CultureInfo.InvariantCulture),
            .. await ServiceConnectArgsAsync(serviceName, ct),
            "--output", "json"
        ]);

        var (exit, output) = await RunAsync(args, ct);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Failed to update ECS service '{serviceName}': {output.Trim()}");
        }

        var doc = JsonDocument.Parse(output);
        return doc.RootElement.GetProperty("service").GetProperty("serviceArn").GetString()!;
    }

    /// <summary>Port mappings (name → port) per service, used for Service Connect.</summary>
    public Dictionary<string, IReadOnlyList<(string Name, int Port)>> ServicePorts { get; } = [];

    /// <summary>
    /// Builds the Service Connect argument when a namespace is configured: the service's
    /// first port is published under the service name. The JSON goes through a temp file
    /// to keep the command line short and readable in logs.
    /// </summary>
    private async Task<string[]> ServiceConnectArgsAsync(string serviceName, CancellationToken ct)
    {
        if (config.ServiceConnectNamespace is null ||
            !ServicePorts.TryGetValue(serviceName, out var ports) || ports.Count == 0)
        {
            return [];
        }

        var (portName, port) = ports[0];
        var json = JsonSerializer.Serialize(new
        {
            enabled = true,
            @namespace = config.ServiceConnectNamespace,
            services = new[]
            {
                new
                {
                    portName,
                    discoveryName = serviceName,
                    clientAliases = new[] { new { port, dnsName = serviceName } }
                }
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"deployify-sc-{serviceName}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json, new System.Text.UTF8Encoding(false), ct);
        return ["--service-connect-configuration", $"file://{path}"];
    }

    /// <summary>
    /// Waits until the service reaches a steady state (ECS gives up after about ten
    /// minutes). Throws when it doesn't, e.g. because tasks keep crashing.
    /// </summary>
    public async Task WaitForStableAsync(string serviceName, CancellationToken ct = default)
    {
        var (exit, output) = await RunAsync(BuildArgs(
            "ecs", "wait", "services-stable", "--cluster", config.ClusterName, "--services", serviceName), ct);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' did not become stable. Check its task logs. {output.Trim()}");
        }
    }

    private string BuildNetworkConfig()
    {
        var subnets = string.Join(",", config.SubnetIds);
        var sgs = string.Join(",", config.SecurityGroupIds);
        return $"awsvpcConfiguration={{subnets=[{subnets}],securityGroups=[{sgs}],assignPublicIp=ENABLED}}";
    }

    internal IReadOnlyList<string> BuildArgs(params string[] command)
        => config.AwsProfile is not null
            ? ["--profile", config.AwsProfile, "--region", config.Region, .. command]
            : ["--region", config.Region, .. command];

    private async Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct, bool sensitive = false)
    {
        var result = await _runner.RunAsync(new ProcessSpec("aws", args) { Log = logger, Sensitive = sensitive }, ct);
        return (result.ExitCode, result.Output);
    }
}
