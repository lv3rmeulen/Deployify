using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Deployify.Target.OvhCloud;

/// <summary>A Managed Kubernetes cluster as <c>GET /cloud/project/{serviceName}/kube/{kubeId}</c> returns it.</summary>
internal sealed record OvhKubeCluster(string Id, string Name, string Region, string Status, string? Version, string? Url)
{
    /// <summary>The cluster accepts work.</summary>
    public bool IsReady => Status.Equals("READY", StringComparison.OrdinalIgnoreCase);

    /// <summary>The cluster is on its way to READY (being created, updated, redeployed or reset).</summary>
    public bool IsTransitioning => Status.ToUpperInvariant() is "INSTALLING" or "UPDATING" or "REDEPLOYING" or "RESETTING" or "REOPENING";
}

/// <summary>
/// The OVHcloud side of a Managed Kubernetes cluster: its status, and a kubeconfig for kubectl.
/// </summary>
internal sealed class OvhKubernetesCluster(OvhApiClient api, string projectId, string clusterId)
{
    private string ClusterPath => $"cloud/project/{Uri.EscapeDataString(projectId)}/kube/{Uri.EscapeDataString(clusterId)}";

    public Task<OvhKubeCluster> GetAsync(CancellationToken ct = default) => api.GetAsync<OvhKubeCluster>(ClusterPath, ct);

    /// <summary>
    /// Waits until the cluster is READY. Fails at once for a cluster in a state it won't leave on
    /// its own (ERROR, SUSPENDED, …), and after <paramref name="timeout"/> for one that is still
    /// changing. With <paramref name="wait"/> false, only READY passes.
    /// </summary>
    public async Task<OvhKubeCluster> WaitUntilReadyAsync(
        bool wait, TimeSpan timeout, TimeSpan pollInterval, ILogger logger, TimeProvider? time = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        var deadline = time.GetUtcNow() + timeout;
        var announced = false;
        while (true)
        {
            var cluster = await GetAsync(ct);
            if (cluster.IsReady)
            {
                return cluster;
            }

            if (!wait || !cluster.IsTransitioning)
            {
                throw new InvalidOperationException(
                    $"OVHcloud cluster '{cluster.Name}' ({cluster.Id}, {cluster.Region}) is {cluster.Status}, not READY.");
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"OVHcloud cluster '{cluster.Name}' is still {cluster.Status} after {timeout.TotalMinutes:0.#} minutes.");
            }

            if (!announced)
            {
                logger.LogInformation("OVHcloud cluster {Name} is {Status}; waiting for it to be READY.", cluster.Name, cluster.Status);
                announced = true;
            }

            await Task.Delay(pollInterval, time, ct);
        }
    }

    /// <summary>
    /// Fetches the cluster's admin kubeconfig (<c>POST …/kubeconfig</c>) and writes it to
    /// <paramref name="path"/>, readable by the current user only where the OS allows it.
    /// </summary>
    public async Task WriteKubeconfigAsync(string path, CancellationToken ct = default)
    {
        var response = await api.PostAsync<KubeconfigResponse>($"{ClusterPath}/kubeconfig", body: null, ct);
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException($"OVHcloud returned an empty kubeconfig for cluster {clusterId}.");
        }

        await WritePrivateFileAsync(path, response.Content, ct);
    }

    /// <summary>Writes a file holding credentials: owner-only on Unix, replaced atomically.</summary>
    internal static async Task WritePrivateFileAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var stream = new FileStream(temp, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content.AsMemory(), ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private sealed record KubeconfigResponse(string Content);
}
