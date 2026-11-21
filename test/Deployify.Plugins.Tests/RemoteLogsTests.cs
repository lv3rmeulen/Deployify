using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Plugins.Tests;

/// <summary>logs/open, logs/next and logs/close, through the real SDK server and the real proxy.</summary>
public sealed class RemoteLogsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InProcessPlugin _plugin = new(p => p
        .Named("logs", "1.0.0")
        .AddTarget<LogsTarget>("logs", "Logs Fake")
        .AddTarget<PlainTarget>("plain", "Plain Fake"));

    private static EnvironmentConfig Config() => new() { Target = DeploymentTargetType.Parse("logs") };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _plugin.DisposeAsync();

    [Fact]
    public async Task TheCapability_IsDeclaredOnlyByTargetsThatReadLogs()
    {
        var logs = _plugin.Target("logs");
        await logs.StartAsync(Ct);

        logs.Info!.Capabilities.ShouldBe(["logs"]);
        ((IDeploymentTarget)logs).GetCapability<IServiceLogSource>().ShouldNotBeNull();
        ((IDeploymentTarget)_plugin.Target("plain")).GetCapability<IServiceLogSource>().ShouldBeNull();
    }

    [Fact]
    public async Task Lines_RoundTrip_WithEveryField_AndTheQueryReachesTheTarget()
    {
        var since = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
        var lines = await _plugin.Target("logs")
            .ReadLogsAsync(Config(), new ServiceLogQuery { Service = "api", Tail = 7, Since = since }, Ct).ToListAsync(Ct);

        lines.ShouldBe(
        [
            new ServiceLogLine { Text = "query tail=7 since=2026-11-21T09:00:00.0000000+00:00 follow=False" },
            new ServiceLogLine { Text = "hello", Instance = "pod-1", Timestamp = since.AddMinutes(1) },
            new ServiceLogLine { Text = "oops", Instance = "pod-2", IsError = true }
        ]);
    }

    [Fact]
    public async Task ManyLines_ComeInSeveralBatches_InOrder()
    {
        var lines = await _plugin.Target("logs").ReadLogsAsync(Config(), new ServiceLogQuery { Service = "many" }, Ct).ToListAsync(Ct);

        lines.Select(l => l.Text).ShouldBe(Enumerable.Range(0, 1234).Select(i => $"line {i}"));
    }

    [Fact]
    public async Task TargetFailures_ComeBackAsServiceLogsExceptions_AfterTheLinesBeforeThem()
    {
        var ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
            await _plugin.Target("logs").ReadLogsAsync(Config(), new ServiceLogQuery { Service = "nope" }, Ct).ToListAsync(Ct));
        ex.Message.ShouldContain("no such service");

        var seen = new List<string>();
        ex = await Should.ThrowAsync<ServiceLogsException>(async () =>
        {
            await foreach (var line in _plugin.Target("logs").ReadLogsAsync(Config(), new ServiceLogQuery { Service = "half" }, Ct))
            {
                seen.Add(line.Text);
            }
        });
        seen.ShouldBe(["before the failure"]);
        ex.Message.ShouldContain("lost the connection");
    }

    [Fact]
    public async Task StoppingToFollow_ClosesTheStreamInThePlugin()
    {
        LogsTarget.Stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await foreach (var _ in _plugin.Target("logs").ReadLogsAsync(Config(), new ServiceLogQuery { Service = "live", Follow = true }, Ct))
        {
            if (++count == 3)
            {
                break;
            }
        }

        await LogsTarget.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}

/// <summary>A target that only reads logs.</summary>
internal sealed class LogsTarget : IDeploymentTarget, IServiceLogSource
{
    public static TaskCompletionSource Stopped { get; set; } = new();

    public DeploymentTargetType TargetType => DeploymentTargetType.Parse("logs");

    public string DisplayName => "Logs Fake";

    public async IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(
        EnvironmentConfig config, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        switch (query.Service)
        {
            case "api":
                yield return new ServiceLogLine
                {
                    Text = $"query tail={query.Tail} since={query.Since:O} follow={query.Follow}"
                };
                yield return new ServiceLogLine { Text = "hello", Instance = "pod-1", Timestamp = query.Since!.Value.AddMinutes(1) };
                yield return new ServiceLogLine { Text = "oops", Instance = "pod-2", IsError = true };
                break;
            case "many":
                for (var i = 0; i < 1234; i++)
                {
                    yield return new ServiceLogLine { Text = $"line {i}" };
                }

                break;
            case "half":
                yield return new ServiceLogLine { Text = "before the failure" };
                throw new InvalidOperationException("lost the connection to the platform");
            case "live":
                try
                {
                    for (var i = 0; ; i++)
                    {
                        await Task.Delay(10, ct);
                        yield return new ServiceLogLine { Text = $"live {i}" };
                    }
                }
                finally
                {
                    Stopped.TrySetResult();
                }

            default:
                throw new ServiceLogsException($"no such service '{query.Service}'");
        }
    }

    public Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((true, null));

    public Task<IReadOnlyList<string>> GenerateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<ValidationResult>> ValidateAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ValidationResult>>([]);

    public Task<PlanResult> PlanAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult(new PlanResult());

    public Task<IReadOnlyList<DeploymentResult>> ApplyAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeploymentResult>>([]);

    public Task PublishAsync(AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceExplanation>>([]);

    public Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceDependency>>([]);

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiffEntry>>([]);
}
