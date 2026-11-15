using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Core.Processes;
using Deployify.Core.ResourceSources;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>What <c>deployify dashboard</c> shows for a Kubernetes environment, and what its commands run.</summary>
public sealed class KubernetesResourceSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ResourceSourceOptions Options = new() { PollInterval = TimeSpan.FromMilliseconds(10) };

    /// <summary>Two services under prefix "shop", the parameters namespace, and someone else's namespace.</summary>
    private const string ManagedObjects = """
    {
      "kind": "List",
      "items": [
        { "kind": "Namespace", "metadata": { "name": "shop-api" } },
        { "kind": "Namespace", "metadata": { "name": "shop-worker" } },
        { "kind": "Namespace", "metadata": { "name": "shop-system" } },
        { "kind": "Namespace", "metadata": { "name": "other-api" } },
        {
          "kind": "Deployment",
          "metadata": {
            "name": "api", "namespace": "shop-api", "creationTimestamp": "2026-11-01T10:00:00Z",
            "annotations": { "deployment.kubernetes.io/revision": "4" }
          },
          "spec": {
            "replicas": 2,
            "template": { "spec": { "containers": [ {
              "name": "api", "image": "registry/api:1.2",
              "envFrom": [ { "configMapRef": { "name": "api-config" } }, { "secretRef": { "name": "api-secrets" } } ],
              "env": [
                { "name": "EXTRA", "value": "1" },
                { "name": "DB_PASSWORD", "valueFrom": { "secretKeyRef": { "name": "api-secrets", "key": "DB_PASSWORD" } } }
              ]
            } ] } }
          },
          "status": {
            "replicas": 2, "readyReplicas": 2, "updatedReplicas": 2,
            "conditions": [ { "type": "Available", "status": "True" } ]
          }
        },
        {
          "kind": "Deployment",
          "metadata": { "name": "worker", "namespace": "shop-worker", "annotations": { "deployify/stopped-replicas": "3" } },
          "spec": { "replicas": 0, "template": { "spec": { "containers": [ { "name": "worker", "image": "registry/worker:1" } ] } } },
          "status": {}
        },
        {
          "kind": "Deployment",
          "metadata": { "name": "api", "namespace": "other-api" },
          "spec": { "replicas": 1 }, "status": {}
        },
        {
          "kind": "ConfigMap",
          "metadata": { "name": "api-config", "namespace": "shop-api" },
          "data": { "ASPNETCORE_URLS": "http://+:8080", "EXTRA": "overridden" }
        },
        {
          "kind": "Service",
          "metadata": { "name": "api", "namespace": "shop-api" },
          "spec": { "ports": [ { "name": "http", "port": 8080 } ] }
        },
        {
          "kind": "Ingress",
          "metadata": { "name": "api", "namespace": "shop-api" },
          "spec": {
            "tls": [ { "hosts": [ "api.example.com" ] } ],
            "rules": [ { "host": "api.example.com", "http": { "paths": [ { "path": "/", "backend": { "service": { "name": "api" } } } ] } } ]
          }
        }
      ]
    }
    """;

    private const string Pods = """
    {
      "items": [
        {
          "metadata": { "name": "api-5d8f-aaaaa", "namespace": "shop-api", "labels": { "app": "api" } },
          "spec": { "nodeName": "node-1", "containers": [ { "name": "api", "image": "registry/api:1.2" } ] },
          "status": {
            "phase": "Running", "startTime": "2026-11-01T10:01:00Z", "podIP": "10.0.0.5",
            "conditions": [ { "type": "Ready", "status": "True" } ],
            "containerStatuses": [ { "name": "api", "restartCount": 0, "state": { "running": {} } } ]
          }
        },
        {
          "metadata": { "name": "api-5d8f-bbbbb", "namespace": "shop-api", "labels": { "app": "api" } },
          "spec": { "containers": [ { "name": "api", "image": "registry/api:1.2" } ] },
          "status": {
            "phase": "Running",
            "conditions": [ { "type": "Ready", "status": "False", "message": "containers not ready" } ],
            "containerStatuses": [ { "name": "api", "restartCount": 7, "state": { "waiting": { "reason": "CrashLoopBackOff" } } } ]
          }
        },
        {
          "metadata": { "name": "api-1234-ccccc", "namespace": "other-api", "labels": { "app": "api" } },
          "spec": {}, "status": { "phase": "Running" }
        }
      ]
    }
    """;

    private static FakeProcessRunner Cluster(string managed = ManagedObjects, string pods = Pods)
        => new FakeProcessRunner()
            .When(["get", "namespaces,deployments,services,ingresses,configmaps"], new ProcessResult(0, managed, ""))
            .When(["get", "pods", "--all-namespaces", "-l", "app"], new ProcessResult(0, pods, ""));

    private static IResourceSource Source(FakeProcessRunner kubectl, string context = "prod")
    {
        var target = new KubernetesDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance)
        {
            Processes = kubectl
        };
        var config = new EnvironmentConfig
        {
            Target = DeploymentTargetType.Kubernetes,
            TargetConfig = new Dictionary<string, object?>
            {
                ["kubernetes"] = JsonSerializer.SerializeToElement(new { namespacePrefix = "shop", context })
            }
        };
        return target.CreateResourceSource(config, Options, NullLoggerFactory.Instance);
    }

    private static async Task<IReadOnlyList<ResourceSnapshot>> FirstSetAsync(IResourceSource source)
    {
        await foreach (var set in source.WatchAsync(Ct))
        {
            return set;
        }

        throw new InvalidOperationException("The source ended without a set.");
    }

    [Fact]
    public async Task Resources_AreTheDeploymentsUnderThePrefix_WithTheirPodsNested()
    {
        await using var source = Source(Cluster());

        var set = await FirstSetAsync(source);

        set.Select(r => (r.Name, r.ResourceType, r.Parent)).ShouldBe(
        [
            ("api", "Deployment", null),
            ("api-5d8f-aaaaa", "Pod", "api"),
            ("api-5d8f-bbbbb", "Pod", "api"),
            ("worker", "Deployment", null)
        ]);
    }

    [Fact]
    public async Task ADeployment_WithACrashingPod_IsUnhealthy_AndShowsItsAddressesAndEnvironment()
    {
        await using var source = Source(Cluster());

        var api = (await FirstSetAsync(source)).Single(r => r.Name == "api");

        api.State.ShouldBe(ResourceStates.RuntimeUnhealthy);
        api.StateStyle.ShouldBe(ResourceStateStyle.Warning);
        api.CreatedAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        api.Urls.ShouldBe(
        [
            new ResourceUrl(null, "https://api.example.com"),
            new ResourceUrl("http", "http://api.shop-api.svc.cluster.local:8080", IsInternal: true)
        ]);

        // envFrom ConfigMap, then env (which wins); secret values are never read.
        api.Environment.ShouldBe(
        [
            new ResourceEnvironmentVariable("ASPNETCORE_URLS", "http://+:8080"),
            new ResourceEnvironmentVariable("DB_PASSWORD", null),
            new ResourceEnvironmentVariable("EXTRA", "1")
        ]);
        api.Properties.Single(p => p.Name == "kubernetes.secrets").Value.ShouldBe("api-secrets");
        api.Properties.Single(p => p.Name == ResourcePropertyNames.ContainerImage).Value.ShouldBe("registry/api:1.2");
        api.Properties.Single(p => p.Name == "kubernetes.revision").Value.ShouldBe("4");
        api.Commands.Select(c => (c.Name, c.Enabled)).ShouldBe([("start", false), ("stop", true), ("restart", true)]);
    }

    [Fact]
    public async Task Pods_ShowTheirOwnState()
    {
        await using var source = Source(Cluster());
        var set = await FirstSetAsync(source);

        var healthy = set.Single(r => r.Name == "api-5d8f-aaaaa");
        healthy.State.ShouldBe(ResourceStates.Running);
        healthy.StateStyle.ShouldBe(ResourceStateStyle.Success);
        healthy.StartedAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 10, 1, 0, TimeSpan.Zero));
        healthy.Properties.Single(p => p.Name == "kubernetes.node").Value.ShouldBe("node-1");

        var crashing = set.Single(r => r.Name == "api-5d8f-bbbbb");
        crashing.State.ShouldBe("CrashLoopBackOff");
        crashing.StateStyle.ShouldBe(ResourceStateStyle.Error);
        crashing.HealthReports.Single().Status.ShouldBe(ResourceHealthStatus.Unhealthy);
        crashing.Properties.Single(p => p.Name == "kubernetes.restarts").Value.ShouldBe("7");
    }

    [Fact]
    public async Task AScaledDownDeployment_IsExited_AndCanBeStarted()
    {
        await using var source = Source(Cluster());

        var worker = (await FirstSetAsync(source)).Single(r => r.Name == "worker");

        worker.State.ShouldBe(ResourceStates.Exited);
        worker.HealthReports.ShouldBeEmpty();
        worker.Commands.Select(c => (c.Name, c.Enabled)).ShouldBe([("start", true), ("stop", false), ("restart", false)]);
    }

    [Fact]
    public async Task EnvironmentValues_CanBeHidden()
    {
        var target = new KubernetesDeploymentTarget(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance)
        {
            Processes = Cluster()
        };
        var config = new EnvironmentConfig
        {
            TargetConfig = new Dictionary<string, object?> { ["kubernetes"] = JsonSerializer.SerializeToElement(new { namespacePrefix = "shop" }) }
        };
        await using var source = target.CreateResourceSource(config, new ResourceSourceOptions { ShowEnvironmentValues = false },
            NullLoggerFactory.Instance);

        var api = (await FirstSetAsync(source)).Single(r => r.Name == "api");

        api.Environment.ShouldAllBe(e => e.Value == null);
    }

    [Fact]
    public async Task Stop_RemembersTheReplicaCount_ThenScalesToZero()
    {
        var kubectl = Cluster();
        await using var source = Source(kubectl);
        await FirstSetAsync(source);

        var result = await source.ExecuteCommandAsync("api", "stop", Ct);

        result.Success.ShouldBeTrue();
        kubectl.Single("annotate", "deployment/api", "-n", "shop-api", "deployify/stopped-replicas=2", "--overwrite");
        kubectl.Single("scale", "deployment/api", "-n", "shop-api", "--replicas=0").Args.ShouldContain("prod");
        kubectl.Calls.FindIndex(c => c.Args.Contains("annotate"))
            .ShouldBeLessThan(kubectl.Calls.FindIndex(c => c.Args.Contains("scale")));
    }

    [Fact]
    public async Task Start_ScalesBackToTheRememberedCount()
    {
        var kubectl = Cluster();
        await using var source = Source(kubectl);
        await FirstSetAsync(source);

        var result = await source.ExecuteCommandAsync("worker", "start", Ct);

        result.Success.ShouldBeTrue();
        kubectl.Single("scale", "deployment/worker", "-n", "shop-worker", "--replicas=3");
        kubectl.Single("annotate", "deployment/worker", "-n", "shop-worker", "deployify/stopped-replicas-");
    }

    [Fact]
    public async Task Restart_RollsTheDeployment_OrDeletesOnePod()
    {
        var kubectl = Cluster();
        await using var source = Source(kubectl);
        await FirstSetAsync(source);

        (await source.ExecuteCommandAsync("api", "restart", Ct)).Success.ShouldBeTrue();
        (await source.ExecuteCommandAsync("api-5d8f-bbbbb", "restart", Ct)).Success.ShouldBeTrue();

        kubectl.Single("rollout", "restart", "deployment/api", "-n", "shop-api");
        kubectl.Single("delete", "pod", "api-5d8f-bbbbb", "-n", "shop-api");
    }

    [Fact]
    public async Task AFailingKubectlCommand_IsReported_NotThrown()
    {
        var kubectl = Cluster().When(["scale"], new ProcessResult(1, "", "forbidden"));
        await using var source = Source(kubectl);
        await FirstSetAsync(source);

        var result = await source.ExecuteCommandAsync("api", "stop", Ct);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull().ShouldContain("forbidden");
    }

    [Fact]
    public async Task Logs_OfADeployment_CoverAllItsPods_AndOfAPod_OnlyThatPod()
    {
        var kubectl = Cluster().When(["logs"], new ProcessResult(0, "2026-11-01T10:00:00Z hello\n", ""));
        await using var source = Source(kubectl);
        await FirstSetAsync(source);

        var lines = new List<ResourceLogLine>();
        await foreach (var line in source.WatchLogsAsync("api", follow: true, Ct))
        {
            lines.Add(line);
        }

        await foreach (var _ in source.WatchLogsAsync("api-5d8f-aaaaa", follow: false, Ct))
        {
        }

        lines.ShouldBe([new ResourceLogLine("2026-11-01T10:00:00Z hello")]);
        var all = kubectl.Single("logs", "-n", "shop-api", "-l", "app=api", "--prefix");
        all.Args.ShouldContain("--follow");
        all.Args.ShouldContain("--timestamps");
        all.Args.ShouldContain("--tail=1000");
        all.Sensitive.ShouldBeTrue();
        kubectl.Single("logs", "-n", "shop-api", "api-5d8f-aaaaa").Args.ShouldNotContain("--follow");
    }

    [Fact]
    public async Task AnUnreachableCluster_YieldsNoSet_InsteadOfAnEmptyOne()
    {
        var kubectl = new FakeProcessRunner { Default = new ProcessResult(1, "", "connection refused") };
        await using var source = Source(kubectl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var sets = 0;
        await foreach (var _ in source.WatchAsync(cts.Token))
        {
            sets++;
        }

        sets.ShouldBe(0);
        kubectl.Calls.Count.ShouldBeGreaterThan(1); // it kept trying
    }
}
