using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Deployify.Target.DigitalOcean.Tests;

/// <summary>An in-memory App Platform and Databases API that records what it was asked to do.</summary>
internal sealed class FakeDigitalOceanApi : IDigitalOceanApi
{
    private int _next;

    public Dictionary<string, DoApp> Apps { get; } = [];
    public Dictionary<string, DoCluster> Clusters { get; } = [];
    public Dictionary<string, List<string>> ClusterDatabases { get; } = [];
    public Dictionary<string, List<string>> TrustedApps { get; } = [];
    public List<string> Calls { get; } = [];

    /// <summary>The phases a new deployment goes through, one per poll.</summary>
    public Queue<string> DeploymentPhases { get; set; } = new(["BUILDING", "DEPLOYING", "ACTIVE"]);

    /// <summary>How many polls a new cluster stays in "creating".</summary>
    public int ClusterPollsUntilOnline { get; set; } = 1;

    public DoProposal Proposal { get; set; } = new(12m, true);
    public DoApiException? ProposeError { get; set; }
    public string? PingError { get; set; }

    private readonly Dictionary<string, int> _clusterPolls = [];
    private readonly Dictionary<string, DoDeployment> _deployments = [];

    public Task<string?> PingAsync(CancellationToken ct) => Task.FromResult(PingError);

    /// <summary>What the logs endpoint answers, and the lines behind each URL.</summary>
    public DoLogUrls LogUrls { get; set; } = new(null, []);
    public Dictionary<string, string[]> LogContent { get; } = [];

    public Task<DoLogUrls> GetRunLogUrlsAsync(string appId, string component, bool follow, int? tailLines, CancellationToken ct)
    {
        Calls.Add($"logs {appId}/{component} follow={follow} tail={tailLines?.ToString() ?? "all"}");
        return Task.FromResult(LogUrls);
    }

    public async IAsyncEnumerable<string> ReadLogLinesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Add($"read {url}");
        foreach (var line in LogContent.GetValueOrDefault(url, []))
        {
            await Task.Yield();
            yield return line;
        }
    }

    public Task<DoApp?> FindAppAsync(string name, CancellationToken ct)
        => Task.FromResult(Apps.Values.FirstOrDefault(a => a.Name == name));

    public Task<DoApp> CreateAppAsync(JsonObject spec, string? projectId, CancellationToken ct)
    {
        Calls.Add($"create app {spec["name"]}");
        var id = $"app-{++_next}";
        var app = new DoApp(id, spec["name"]!.GetValue<string>(), spec, $"https://{spec["name"]}.ondigitalocean.app", NewDeployment());
        Apps[id] = app;
        return Task.FromResult(app);
    }

    public Task<DoApp> UpdateAppAsync(string appId, JsonObject spec, CancellationToken ct)
    {
        Calls.Add($"update app {appId}");
        var app = Apps[appId] with { Spec = spec, PendingDeploymentId = NewDeployment() };
        Apps[appId] = app;
        return Task.FromResult(app);
    }

    public Task DeleteAppAsync(string appId, CancellationToken ct)
    {
        Calls.Add($"delete app {appId}");
        Apps.Remove(appId);
        return Task.CompletedTask;
    }

    public Task<DoProposal> ProposeAsync(JsonObject spec, string? appId, CancellationToken ct)
    {
        Calls.Add($"propose {appId ?? "new"}");
        return ProposeError is { } error ? Task.FromException<DoProposal>(error) : Task.FromResult(Proposal);
    }

    public Task<DoDeployment?> GetDeploymentAsync(string appId, string deploymentId, CancellationToken ct)
    {
        if (!_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return Task.FromResult<DoDeployment?>(null);
        }

        if (!deployment.IsFinished && DeploymentPhases.Count > 0)
        {
            deployment = _deployments[deploymentId] = deployment with { Phase = DeploymentPhases.Dequeue() };
        }

        return Task.FromResult<DoDeployment?>(deployment);
    }

    public Task<DoDeployment?> GetLatestDeploymentAsync(string appId, CancellationToken ct)
        => Task.FromResult(_deployments.Values.LastOrDefault());

    public Task<DoCluster?> FindClusterAsync(string name, CancellationToken ct)
        => Task.FromResult(Clusters.Values.FirstOrDefault(c => c.Name == name));

    public Task<DoCluster?> GetClusterAsync(string id, CancellationToken ct)
    {
        if (!Clusters.TryGetValue(id, out var cluster))
        {
            return Task.FromResult<DoCluster?>(null);
        }

        var polls = _clusterPolls[id] = _clusterPolls.GetValueOrDefault(id) + 1;
        if (polls >= ClusterPollsUntilOnline)
        {
            cluster = Clusters[id] = cluster with { Status = "online" };
        }

        return Task.FromResult<DoCluster?>(cluster);
    }

    public Task<IReadOnlyList<DoCluster>> ListClustersAsync(string tag, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DoCluster>>([.. Clusters.Values.Where(c => c.Tags.Contains(tag))]);

    public Task<DoCluster> CreateClusterAsync(DoClusterRequest request, CancellationToken ct)
    {
        Calls.Add($"create cluster {request.Name} {request.Engine} {request.Version ?? "-"} {request.Region} {request.Size} {request.Nodes}");
        var cluster = AddCluster(request.Name, request.Engine, "creating", request.Region, request.Tags);
        return Task.FromResult(cluster);
    }

    public DoCluster AddCluster(string name, string engine, string status = "online", string region = "ams3", IReadOnlyList<string>? tags = null)
    {
        var cluster = new DoCluster($"db-{++_next}", name, engine, status, region, tags ?? []);
        Clusters[cluster.Id] = cluster;
        ClusterDatabases[cluster.Id] = ["defaultdb"];
        return cluster;
    }

    public Task DeleteClusterAsync(string id, CancellationToken ct)
    {
        Calls.Add($"delete cluster {Clusters[id].Name}");
        Clusters.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListClusterDatabasesAsync(string clusterId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(ClusterDatabases[clusterId]);

    public Task CreateClusterDatabaseAsync(string clusterId, string name, CancellationToken ct)
    {
        Calls.Add($"create database {name} in {Clusters[clusterId].Name}");
        ClusterDatabases[clusterId].Add(name);
        return Task.CompletedTask;
    }

    public Task AllowAppAsync(string clusterId, string appId, CancellationToken ct)
    {
        Calls.Add($"trust {appId} on {Clusters[clusterId].Name}");
        TrustedApps.TryAdd(clusterId, []);
        TrustedApps[clusterId].Add(appId);
        return Task.CompletedTask;
    }

    public static DoApiException Rejection(string message)
        => new(HttpStatusCode.UnprocessableEntity, "unprocessable_entity", message);

    private string NewDeployment()
    {
        var id = $"dep-{++_next}";
        _deployments[id] = new DoDeployment(id, "PENDING_BUILD");
        return id;
    }
}
