using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Deployify.Core.Interfaces;

namespace Deployify.Target.Compose;

/// <summary>The parts of <c>docker inspect</c> on a Compose container the rollout needs.</summary>
public sealed record ComposeContainerStatus(
    string Id,
    string ImageId,
    string Status,
    string? Health,
    int RestartCount,
    int ExitCode,
    string? ConfigFiles)
{
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";

    public static IReadOnlyList<ComposeContainerStatus> ParseInspect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<ComposeContainerStatus>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var state = c.TryGetProperty("State", out var s) ? s : default;
            string? health = null;
            if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Health", out var h) &&
                h.ValueKind == JsonValueKind.Object)
            {
                health = Str(h, "Status");
            }

            string? configFiles = null;
            if (c.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Object &&
                cfg.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                configFiles = Str(labels, ConfigFilesLabel);
            }

            result.Add(new ComposeContainerStatus(
                Str(c, "Id") ?? "",
                Str(c, "Image") ?? "",
                Str(state, "Status") ?? "unknown",
                health,
                Int(c, "RestartCount"),
                Int(state, "ExitCode"),
                configFiles));
        }

        return result;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;
}

/// <summary>
/// Health of a Compose service. A revision is the image ID (<c>sha256:…</c>) its containers run,
/// which, unlike a tag such as <c>latest</c>, can't move to another image.
/// </summary>
internal static class ComposeRolloutHealth
{
    public static HealthObservation Evaluate(IReadOnlyList<ComposeContainerStatus> containers, string imageId)
    {
        if (containers.Count == 0)
        {
            return HealthObservation.Progressing("waiting for the container to be created");
        }

        var ours = containers.Where(c => c.ImageId == imageId).ToList();
        if (ours.Count == 0)
        {
            return HealthObservation.Failed($"the service runs {Short(containers[0].ImageId)} instead of {Short(imageId)}");
        }

        if (ours.FirstOrDefault(c => c.Status is "exited" or "dead") is { } stopped)
        {
            return HealthObservation.Failed($"the container exited with code {stopped.ExitCode}");
        }

        if (ours.FirstOrDefault(c => c.RestartCount >= 2) is { } looping)
        {
            return HealthObservation.Failed(
                $"the container keeps restarting ({looping.RestartCount} restarts, last exit code {looping.ExitCode})");
        }

        if (ours.Any(c => c.Health == "unhealthy"))
        {
            return HealthObservation.Failed("the container's healthcheck is failing");
        }

        if (ours.FirstOrDefault(c => c.Status != "running") is { } notRunning)
        {
            return HealthObservation.Progressing($"the container is {notRunning.Status}");
        }

        if (ours.Any(c => c.Health == "starting"))
        {
            return HealthObservation.Progressing("waiting for the healthcheck");
        }

        if (containers.Any(c => c.ImageId != imageId && c.Status == "running"))
        {
            return HealthObservation.Progressing("an old container is still running");
        }

        var checkedHealth = ours.All(c => c.Health == "healthy");
        return HealthObservation.Healthy(
            $"{ours.Count} container(s) running" + (checkedHealth ? ", healthy" : " (no healthcheck)"));
    }

    /// <summary>An override file that pins one service to an image ID and never pulls it.</summary>
    public static string PinImageOverride(string service, string imageId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Written by deployify while rolling back; not used by a normal apply.");
        sb.AppendLine("services:");
        sb.AppendLine($"  {Quote(service)}:");
        sb.AppendLine($"    image: {Quote(imageId)}");
        sb.AppendLine("    pull_policy: never");
        return sb.ToString();
    }

    public static string Short(string imageId)
        => imageId.StartsWith("sha256:", StringComparison.Ordinal) && imageId.Length > 19 ? imageId[..19] : imageId;

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
