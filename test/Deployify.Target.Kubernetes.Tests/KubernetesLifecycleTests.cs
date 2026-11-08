using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Deployify.Target.Kubernetes.Tests;

/// <summary>What destroy and prune do on Kubernetes: a service is its labelled namespace.</summary>
public sealed class KubernetesLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KubernetesDeploymentTarget Target(FakeProcessRunner fake)
        => new(Substitute.For<IPublisher>(), NullLogger<KubernetesDeploymentTarget>.Instance) { Processes = fake };

    /// <summary>kubectl answering the managed-namespace query with <paramref name="namespaces"/>.</summary>
    private static FakeProcessRunner Cluster(params string[] namespaces)
        => new FakeProcessRunner().When(["get", "namespaces", "-l", "app.kubernetes.io/managed-by=deployify"],
            new ProcessResult(0, string.Join(' ', namespaces), ""));

    [Fact]
    public async Task ListManaged_IsTheLabelledNamespacesUnderThePrefix()
    {
        var managed = await Target(Cluster("test-api", "test-worker", "other-app")).ListManagedAsync(Fixtures.KubernetesConfig(), Ct);

        managed.Select(m => (m.Name, m.TargetId)).ShouldBe([("api", "test-api"), ("worker", "test-worker")]);
    }

    [Fact]
    public async Task ListManaged_Throws_WhenTheClusterCantBeReached()
    {
        var fake = new FakeProcessRunner().When(["get", "namespaces"], new ProcessResult(1, "", "Unable to connect to the server"));

        await Should.ThrowAsync<InvalidOperationException>(() => Target(fake).ListManagedAsync(Fixtures.KubernetesConfig(), Ct));
    }

    [Fact]
    public async Task Delete_DeletesTheServiceNamespace_AndWaitsForIt()
    {
        var fake = Cluster("test-api");

        await Target(fake).DeleteServiceAsync(Fixtures.KubernetesConfig(), "api", new DeleteOptions(), Ct);

        var args = fake.Single("delete", "namespace").Args.ToList();
        args[args.IndexOf("namespace") + 1].ShouldBe("test-api");
        args.ShouldContain("--wait=true");
    }

    [Fact]
    public async Task Delete_NeverDeletesANamespaceWithoutDeployifysLabel()
    {
        // test-api exists but isn't labelled, so the managed query doesn't return it.
        var fake = Cluster("test-worker");

        await Target(fake).DeleteServiceAsync(Fixtures.KubernetesConfig(), "api", new DeleteOptions(), Ct);

        fake.Calls.ShouldNotContain(c => c.Args.Contains("delete"));
    }

    [Fact]
    public async Task ADeleteThatFails_Throws_WithKubectlsError()
    {
        var fake = Cluster("test-api").When(["delete", "namespace"], new ProcessResult(1, "", "Error from server (Forbidden)"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Target(fake).DeleteServiceAsync(Fixtures.KubernetesConfig(), "api", new DeleteOptions(), Ct));
        ex.Message.ShouldContain("Forbidden");
    }

    [Fact]
    public async Task WithoutKubectl_DestroyRefusesClearly()
    {
        var fake = new FakeProcessRunner();
        fake.Missing.Add("kubectl");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Target(fake).ListManagedAsync(Fixtures.KubernetesConfig(), Ct));
        ex.Message.ShouldContain("kubectl");
    }
}
