using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Deployify.Core.Interfaces;

namespace Deployify.Target.AwsEcs;

/// <summary>The parts of <c>aws ecs describe-services</c> the rollout needs.</summary>
public sealed record EcsServiceStatus(
    string Status,
    string? TaskDefinition,
    int DesiredCount,
    IReadOnlyList<EcsDeploymentStatus> Deployments)
{
    /// <summary>
    /// Parses a describe-services response. Returns null when the service doesn't exist
    /// (ECS answers with an empty <c>services</c> list and a MISSING failure).
    /// </summary>
    public static EcsServiceStatus? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var services = doc.RootElement.GetProperty("services");
        if (services.GetArrayLength() == 0)
        {
            return null;
        }

        var s = services[0];
        var deployments = new List<EcsDeploymentStatus>();
        if (s.TryGetProperty("deployments", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in deps.EnumerateArray())
            {
                deployments.Add(new EcsDeploymentStatus(
                    Str(d, "status") ?? "",
                    Str(d, "taskDefinition"),
                    Str(d, "rolloutState"),
                    Str(d, "rolloutStateReason"),
                    Num(d, "desiredCount"),
                    Num(d, "runningCount"),
                    Num(d, "failedTasks")));
            }
        }

        return new EcsServiceStatus(Str(s, "status") ?? "", Str(s, "taskDefinition"), Num(s, "desiredCount"), deployments);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

public sealed record EcsDeploymentStatus(
    string Status,
    string? TaskDefinition,
    string? RolloutState,
    string? RolloutStateReason,
    int DesiredCount,
    int RunningCount,
    int FailedTasks);

/// <summary>Turns a describe-services snapshot into a health observation for one task definition.</summary>
internal static class EcsRolloutHealth
{
    public static HealthObservation Evaluate(EcsServiceStatus? status, string taskDefinitionArn, bool circuitBreaker)
    {
        if (status is null || status.Status == "INACTIVE")
        {
            return HealthObservation.Failed("the ECS service does not exist");
        }

        var primary = status.Deployments.FirstOrDefault(d => d.Status == "PRIMARY");
        if (primary is null)
        {
            return HealthObservation.Progressing("waiting for ECS to start the deployment");
        }

        if (primary.TaskDefinition != taskDefinitionArn)
        {
            var ours = status.Deployments.FirstOrDefault(d => d.TaskDefinition == taskDefinitionArn);
            var why = ours?.RolloutStateReason is { } r ? $": {r}" : "";
            return circuitBreaker
                ? HealthObservation.Reverted($"the ECS circuit breaker rolled back to {Short(primary.TaskDefinition)}{why}")
                : HealthObservation.Failed($"{Short(primary.TaskDefinition)} replaced {Short(taskDefinitionArn)}{why}");
        }

        if (primary.RolloutState == "FAILED")
        {
            return HealthObservation.Failed(primary.RolloutStateReason ?? "the ECS deployment failed");
        }

        var detail = $"{primary.RunningCount}/{primary.DesiredCount} task(s) running";
        if (primary.FailedTasks > 0)
        {
            detail += $", {primary.FailedTasks} failed to start";
        }

        // rolloutState is only reported for the ECS deployment controller; without it, a single
        // deployment with every task running is the steady state.
        var settled = primary.RolloutState == "COMPLETED" ||
                      (primary.RolloutState is null && status.Deployments.Count == 1);
        return settled && primary.RunningCount >= primary.DesiredCount
            ? HealthObservation.Healthy(detail)
            : HealthObservation.Progressing(detail);
    }

    /// <summary><c>arn:…:task-definition/api:42</c> → <c>api:42</c>.</summary>
    public static string Short(string? arn)
        => arn is null ? "(none)" : arn[(arn.LastIndexOf('/') + 1)..];
}
