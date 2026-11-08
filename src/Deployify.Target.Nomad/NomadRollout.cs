using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// Health-gated rollout for Nomad jobs, shared by the Nomad and Nomad+Consul targets.
/// A revision is a job version ("3"). Restoring reverts the job, which registers the old spec
/// as a new version, so <see cref="RestoreAsync"/> reports the version to wait for.
/// </summary>
public sealed class NomadRollout(ILogger logger)
{
    /// <summary>
    /// Job secrets (Nomad Variables) as they were before the rollout, held in memory for a rollback
    /// in the same run. For later rollbacks a copy is also kept in Nomad itself, under
    /// <see cref="BackupRoot"/>, and the journal records only its path.
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, string>?> _variablesBefore = new();

    /// <summary>Where copies of job secrets are kept for rollbacks: <c>deployify/rollback/{job}/v{version}</c>.</summary>
    public const string BackupRoot = "deployify/rollback";

    /// <summary>Backups kept per job; older ones are deleted when a new one is written.</summary>
    public const int BackupsKept = 5;

    private const string SecretsBackupKey = "secretsBackup";
    private const string SecretsBeforeKey = "secretsBefore";

    public async Task<ServiceSnapshot> CaptureAsync(NomadClient client, string @namespace, string job, CancellationToken ct)
    {
        var info = await client.GetJobAsync(job, ct);

        var key = VariableKey(@namespace, job);
        var data = new Dictionary<string, string>();
        try
        {
            var before = await client.GetVariableItemsAsync(NomadVariables.JobPath(job), ct);
            _variablesBefore[key] = before;
            if (before is null)
            {
                data[SecretsBeforeKey] = "none";
            }
            else if (await BackUpAsync(client, job, info, before, ct) is { } backupPath)
            {
                data[SecretsBackupKey] = backupPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _variablesBefore.TryRemove(key, out _);
            logger.LogWarning("Could not read the secrets of {Job} ({Message}); a rollback will not restore them.",
                job, ex.Message);
        }

        return info is null || info.Stop
            ? new ServiceSnapshot { ServiceName = job, Existed = false, Data = data }
            : new ServiceSnapshot
            {
                ServiceName = job,
                Existed = true,
                Revision = info.Version.ToString(CultureInfo.InvariantCulture),
                Data = data
            };
    }

    /// <summary>
    /// Copies the job's secrets to <c>deployify/rollback/{job}/v{version}</c>, a path the job's
    /// own tasks can't read, and deletes all but the newest <see cref="BackupsKept"/> copies.
    /// Returns the path, or null when the copy couldn't be written (the rollback then only works
    /// in the same run).
    /// </summary>
    private async Task<string?> BackUpAsync(
        NomadClient client, string job, NomadJobInfo? info, Dictionary<string, string> items, CancellationToken ct)
    {
        var prefix = $"{BackupRoot}/{job}/";
        var path = prefix + (info is null ? "new" : $"v{info.Version.ToString(CultureInfo.InvariantCulture)}");
        try
        {
            await client.PutVariableAsync(path, items, ct);

            var backups = await client.ListVariablePathsAsync(prefix, ct);
            foreach (var old in backups
                         .Select(p => (Path: p, Version: long.TryParse(p[prefix.Length..].TrimStart('v'),
                             NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1))
                         .OrderByDescending(b => b.Version)
                         .Skip(BackupsKept))
            {
                await client.DeleteVariableAsync(old.Path, ct);
            }

            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not keep a copy of the secrets of {Job} in Nomad ({Message}); only a rollback " +
                              "in this run will restore them.", job, ex.Message);
            return null;
        }
    }

    /// <summary>The version Nomad registered for the job just submitted.</summary>
    public static async Task<string> CurrentVersionAsync(NomadClient client, string job, CancellationToken ct)
    {
        var info = await client.GetJobAsync(job, ct)
                   ?? throw new InvalidOperationException($"Job '{job}' was submitted but Nomad doesn't list it.");
        return info.Version.ToString(CultureInfo.InvariantCulture);
    }

    public static async Task<HealthObservation> ObserveAsync(
        NomadClient client, string job, string revision, bool autoRevert, CancellationToken ct)
    {
        var info = await client.GetJobAsync(job, ct);
        var deployments = await client.GetDeploymentsAsync(job, ct);
        return Evaluate(info, deployments, ParseVersion(revision), autoRevert);
    }

    public async Task<string?> RestoreAsync(
        NomadClient client, string @namespace, ServiceSnapshot snapshot, CancellationToken ct)
    {
        var job = snapshot.ServiceName;

        // Secrets first, so the reverted allocations render the old values.
        await RestoreVariablesAsync(client, @namespace, snapshot, ct);

        var info = await client.GetJobAsync(job, ct);
        if (snapshot is not { Existed: true, Revision: { } revision })
        {
            if (info is { Stop: false })
            {
                await client.StopJobAsync(job, purge: false, ct);
                logger.LogInformation("Stopped new job {Job}.", job);
            }

            return null;
        }

        var previous = ParseVersion(revision);
        if (info is null)
        {
            throw new InvalidOperationException($"Job '{job}' no longer exists in Nomad, so it can't be reverted.");
        }

        if (info.Version == previous && !info.Stop)
        {
            return revision; // the rollout never changed the job
        }

        await client.RevertJobAsync(job, previous, ct);
        var reverted = await CurrentVersionAsync(client, job, ct);
        logger.LogInformation("Reverted {Job} to version {Previous} (now version {Reverted}).", job, previous, reverted);
        return reverted;
    }

    private async Task RestoreVariablesAsync(
        NomadClient client, string @namespace, ServiceSnapshot snapshot, CancellationToken ct)
    {
        var job = snapshot.ServiceName;
        Dictionary<string, string>? before;
        if (_variablesBefore.TryGetValue(VariableKey(@namespace, job), out var inMemory))
        {
            before = inMemory;
        }
        else if (snapshot.Data.TryGetValue(SecretsBeforeKey, out var marker) && marker == "none")
        {
            before = null;
        }
        else if (snapshot.Data.TryGetValue(SecretsBackupKey, out var backupPath))
        {
            // A rollback from a later process: read the copy kept in Nomad.
            before = await client.GetVariableItemsAsync(backupPath, ct)
                     ?? throw new InvalidOperationException(
                         $"The copy of {job}'s secrets at {backupPath} is gone, so they can't be restored.");
        }
        else
        {
            return; // never captured
        }

        var path = NomadVariables.JobPath(job);
        var now = await client.GetVariableItemsAsync(path, ct);
        if (SameItems(before, now))
        {
            return; // unchanged: rewriting would restart the tasks for nothing
        }

        if (before is null)
        {
            await client.DeleteVariableAsync(path, ct);
        }
        else
        {
            await client.PutVariableAsync(path, before, ct);
        }

        logger.LogInformation("Restored the secrets of {Job}.", job);
    }

    /// <summary>Turns a job and its deployments into a health observation for one job version.</summary>
    public static HealthObservation Evaluate(
        NomadJobInfo? job, IReadOnlyList<NomadDeploymentInfo> deployments, long version, bool autoRevert)
    {
        if (job is null)
        {
            return HealthObservation.Failed("the Nomad job is not registered");
        }

        if (job.Stop)
        {
            return HealthObservation.Failed("the Nomad job was stopped");
        }

        var ours = deployments.FirstOrDefault(d => d.JobVersion == version);
        var newer = deployments.Where(d => d.JobVersion > version).OrderByDescending(d => d.JobVersion).FirstOrDefault();

        if (ours is null)
        {
            if (newer is not null)
            {
                return HealthObservation.Failed($"version {newer.JobVersion} replaced version {version}");
            }

            // A job without an update block (or one Nomad didn't need to redeploy) has no deployment.
            if (deployments.Count == 0 && job.Version == version && job.Status == "running")
            {
                return HealthObservation.Healthy("running (no deployment to watch)");
            }

            return HealthObservation.Progressing($"waiting for Nomad to start deploying version {version}");
        }

        var counts = $"{ours.HealthyAllocs}/{ours.DesiredTotal} allocation(s) healthy" +
                     (ours.UnhealthyAllocs > 0 ? $", {ours.UnhealthyAllocs} unhealthy" : "");
        var why = string.IsNullOrWhiteSpace(ours.StatusDescription) ? null : ours.StatusDescription;

        switch (ours.Status)
        {
            case "successful":
                return HealthObservation.Healthy(counts);
            case "failed":
                if (autoRevert && newer is not null)
                {
                    return HealthObservation.Reverted(
                        $"Nomad rolled back to an earlier version (now version {newer.JobVersion}): {why ?? "deployment failed"}",
                        newer.JobVersion.ToString(CultureInfo.InvariantCulture));
                }

                if (autoRevert && why is not null && why.Contains("rolling back", StringComparison.OrdinalIgnoreCase))
                {
                    return HealthObservation.Progressing($"Nomad is rolling back: {why}");
                }

                return HealthObservation.Failed(why ?? "the Nomad deployment failed");
            case "cancelled":
                return HealthObservation.Failed(newer is not null
                    ? $"cancelled: version {newer.JobVersion} was submitted"
                    : why ?? "the Nomad deployment was cancelled");
            default:
                return HealthObservation.Progressing(counts);
        }
    }

    public static long ParseVersion(string revision)
        => long.TryParse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"'{revision}' is not a Nomad job version.");

    private static string VariableKey(string @namespace, string job) => $"{@namespace}/{job}";

    private static bool SameItems(Dictionary<string, string>? a, Dictionary<string, string>? b)
        => a is null || b is null
            ? a is null && b is null
            : a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
