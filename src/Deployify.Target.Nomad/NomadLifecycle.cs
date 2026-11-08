using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.Nomad;

/// <summary>
/// Destroy and prune for Nomad, shared by the Nomad and Nomad + Consul targets. Ownership is
/// the <see cref="NomadClient.ManagedMetaKey"/> job meta every generated job carries; deleting
/// a service purges its job and, unless secrets are kept, its Nomad Variable and the rollback
/// copies of it.
/// </summary>
public static class NomadLifecycle
{
    public static async Task<IReadOnlyList<ManagedService>> ListManagedAsync(NomadClient client, CancellationToken ct)
        => [.. (await client.GetManagedJobNamesAsync(ct)).Select(n => new ManagedService(n))];

    public static async Task DeleteServiceAsync(
        NomadClient client, string job, DeleteOptions options, ILogger logger, CancellationToken ct)
    {
        if (await client.GetJobAsync(job, ct) is not null)
        {
            await client.StopJobAsync(job, purge: true, ct);
            logger.LogInformation("Purged Nomad job {Job}.", job);
        }

        if (options.KeepSecrets)
        {
            return;
        }

        var paths = new List<string>();
        if (await client.GetVariableItemsAsync(NomadVariables.JobPath(job), ct) is not null)
        {
            paths.Add(NomadVariables.JobPath(job));
        }

        paths.AddRange(await client.ListVariablePathsAsync($"{NomadRollout.BackupRoot}/{job}/", ct));
        foreach (var path in paths)
        {
            await client.DeleteVariableAsync(path, ct);
            logger.LogInformation("Deleted Nomad variable {Path}.", path);
        }
    }

    public static IReadOnlyList<string> DescribeKept(string @namespace, DeleteOptions options)
    {
        var kept = new List<string> { $"the Nomad namespace '{@namespace}' and anything not created by Deployify" };
        if (options.KeepSecrets)
        {
            kept.Add("the services' Nomad Variables (--keep-secrets)");
        }

        return kept;
    }
}
