using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.ServiceLogs;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Methods = Deployify.Plugin.Contracts.PluginProtocol.Methods;

namespace Deployify.Plugins;

/// <summary>
/// A target served by a plugin process. Implements every target interface, but only the
/// capabilities the plugin declared are offered through <see cref="GetCapability"/>, which is
/// how commands ask for them (<c>target.GetCapability&lt;IRolloutTarget&gt;()</c>).
/// </summary>
public sealed class RemoteDeploymentTarget
    : IDeploymentTarget, IRolloutTarget, IResourceLifecycle, IServiceLogSource,
      ICapabilityProvider, IStartableTarget, IAsyncDisposable
{
    private readonly PluginHost _host;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnvironmentConfig, object> _checked = [];
    private PluginTargetInfo? _info;

    public RemoteDeploymentTarget(PluginHost host, DeploymentTargetType targetType)
    {
        _host = host;
        TargetType = targetType;
    }

    public DeploymentTargetType TargetType { get; }

    public string DisplayName => _info?.DisplayName ?? $"{TargetType} (plugin {_host.Spec.DisplayName})";

    /// <summary>What the plugin declared for this target; null until started.</summary>
    public PluginTargetInfo? Info => _info;

    /// <summary>The resource types the plugin declared; empty until started.</summary>
    public IReadOnlyCollection<string> AdditionalResourceTypes => _info?.ResourceTypes ?? [];

    /// <summary>Starts the plugin and checks that it serves this target.</summary>
    public async Task StartAsync(CancellationToken ct = default) => await ConnectAsync(ct);

    public object? GetCapability(Type capability)
    {
        if (capability == typeof(IDeploymentTarget))
        {
            return this;
        }

        // Callers normally start the target first (the session resolver does); a target used
        // directly finds out what it supports here, once.
        if (_info is null)
        {
            Task.Run(() => ConnectAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }

        var info = _info!;
        var name = capability == typeof(IRolloutTarget) ? PluginProtocol.Capabilities.Rollout
            : capability == typeof(IResourceLifecycle) ? PluginProtocol.Capabilities.Lifecycle
            : capability == typeof(IServiceLogSource) ? PluginProtocol.Capabilities.Logs
            : null;
        return name is not null && info.Capabilities.Contains(name, StringComparer.OrdinalIgnoreCase) ? this : null;
    }

    // ── IDeploymentTarget ────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> PingAsync(EnvironmentConfig config, CancellationToken ct = default)
    {
        PingResult result;
        try
        {
            result = await CallAsync<PingResult>(Methods.Ping, null, config, null, ct);
        }
        catch (PluginException ex)
        {
            return (false, ex.Message);
        }

        return (result.Ok, result.Error);
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => (await CallAsync<GenerateResult>(Methods.Generate, manifest, config, outputDirectory, ct)).Files;

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => [.. (await CallAsync<ArtifactValidation[]>(Methods.Validate, manifest, config, outputDirectory, ct)).Select(WireMapping.FromWire)];

    public async Task<PlanResult> PlanAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => WireMapping.FromWire(await CallAsync<PlanInfo>(Methods.Plan, manifest, config, outputDirectory, ct));

    public async Task<IReadOnlyList<DeploymentResult>> ApplyAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => [.. (await CallAsync<ServiceDeploymentInfo[]>(Methods.Apply, manifest, config, outputDirectory, ct)).Select(WireMapping.FromWire)];

    public async Task PublishAsync(
        AspireManifest manifest, EnvironmentConfig config, string appHostDirectory, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        Prepare(manifest, config);
        await connection.InvokeAsync<object?>(Methods.Publish, new PublishRequest
        {
            TargetId = TargetType.Value,
            Manifest = WireMapping.ToWire(manifest),
            Environment = WireMapping.ToWire(config),
            AppHostDirectory = appHostDirectory
        }, ct);
    }

    public async Task<IReadOnlyList<ServiceExplanation>> ExplainAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => [.. (await CallAsync<ServiceExplanationInfo[]>(Methods.Explain, manifest, config, null, ct)).Select(WireMapping.FromWire)];

    public async Task<IReadOnlyList<ServiceDependency>> GetDependenciesAsync(
        AspireManifest manifest, EnvironmentConfig config, CancellationToken ct = default)
        => [.. (await CallAsync<ServiceDependencyInfo[]>(Methods.Dependencies, manifest, config, null, ct)).Select(WireMapping.FromWire)];

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => [.. (await CallAsync<DiffEntryInfo[]>(Methods.Diff, manifest, config, outputDirectory, ct)).Select(WireMapping.FromWire)];

    // ── IRolloutTarget ───────────────────────────────────────────────────────

    public async Task<ServiceSnapshot> CaptureAsync(
        AspireManifest manifest, EnvironmentConfig config, string service, CancellationToken ct = default)
        => WireMapping.FromWire(await ServiceCallAsync<ServiceSnapshotInfo>(Methods.RolloutCapture, manifest, config, null, service, null, ct));

    public async Task<AppliedRevision> ApplyServiceAsync(
        AspireManifest manifest, EnvironmentConfig config, string outputDirectory, string service, CancellationToken ct = default)
        => WireMapping.FromWire(await ServiceCallAsync<AppliedRevisionInfo>(
            Methods.RolloutApplyService, manifest, config, outputDirectory, service, null, ct));

    public async Task<HealthObservation> ObserveAsync(
        EnvironmentConfig config, string service, string revision, CancellationToken ct = default)
        => WireMapping.FromWire(await ServiceCallAsync<HealthObservationInfo>(
            Methods.RolloutObserve, null, config, null, service, revision, ct));

    public async Task<string?> RestoreAsync(EnvironmentConfig config, ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        Prepare(null, config);
        var result = await connection.InvokeAsync<RestoreResult>(Methods.RolloutRestore, new RestoreRequest
        {
            TargetId = TargetType.Value,
            Environment = WireMapping.ToWire(config),
            Snapshot = WireMapping.ToWire(snapshot)
        }, ct);
        return result.Revision;
    }

    public async Task<bool> HasNativeRollbackAsync(EnvironmentConfig config, CancellationToken ct = default)
        => (await CallAsync<BoolResult>(Methods.RolloutNativeRollback, null, config, null, ct)).Value;

    public async Task<string?> GetUnavailableReasonAsync(EnvironmentConfig config, CancellationToken ct = default)
        => (await CallAsync<ReasonResult>(Methods.RolloutUnavailableReason, null, config, null, ct)).Reason;

    public Task CompleteAsync(AspireManifest manifest, EnvironmentConfig config, string outputDirectory, CancellationToken ct = default)
        => CallAsync<object?>(Methods.RolloutComplete, manifest, config, outputDirectory, ct);

    // ── IResourceLifecycle ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<ManagedService>> ListManagedAsync(EnvironmentConfig config, CancellationToken ct = default)
        => [.. (await CallAsync<ManagedServiceInfo[]>(Methods.LifecycleListManaged, null, config, null, ct)).Select(WireMapping.FromWire)];

    public Task DeleteServiceAsync(EnvironmentConfig config, string service, DeleteOptions options, CancellationToken ct = default)
        => DeleteCallAsync<object?>(Methods.LifecycleDeleteService, config, service, options, ct);

    public async Task<IReadOnlyList<string>> DestroyEnvironmentAsync(
        EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => (await DeleteCallAsync<LinesResult>(Methods.LifecycleDestroyEnvironment, config, null, options, ct)).Lines;

    public async Task<IReadOnlyList<string>> DescribeKeptAsync(
        EnvironmentConfig config, DeleteOptions options, CancellationToken ct = default)
        => (await DeleteCallAsync<LinesResult>(Methods.LifecycleDescribeKept, config, null, options, ct)).Lines;

    // ── IServiceLogSource ────────────────────────────────────────────────────

    /// <summary>
    /// Opens a log stream in the plugin and reads it a batch at a time; each <c>logs/next</c>
    /// waits up to a second for lines, so following doesn't busy-poll. A reader that stops early
    /// (Ctrl+C) closes the stream.
    /// </summary>
    public async IAsyncEnumerable<ServiceLogLine> ReadLogsAsync(
        EnvironmentConfig config, ServiceLogQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct);
        Prepare(null, config);
        var open = await LogsCallAsync<LogsOpenResult>(connection, Methods.LogsOpen, new LogsOpenRequest
        {
            TargetId = TargetType.Value,
            Environment = WireMapping.ToWire(config),
            Service = query.Service,
            Tail = query.Tail,
            Since = query.Since,
            Follow = query.Follow
        }, ct);

        var done = false;
        try
        {
            while (!done)
            {
                var batch = await LogsCallAsync<LogsBatch>(
                    connection, Methods.LogsNext, new LogsNextRequest { StreamId = open.StreamId }, ct);
                done = batch.Done;
                foreach (var line in batch.Lines)
                {
                    yield return new ServiceLogLine
                    {
                        Text = line.Text,
                        Instance = line.Instance,
                        Timestamp = line.Timestamp,
                        IsError = line.IsError
                    };
                }
            }
        }
        finally
        {
            if (!done)
            {
                // Best effort: the plugin may already be gone, and the session closes every stream anyway.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await connection.InvokeAsync<object?>(Methods.LogsClose, new LogsCloseRequest { StreamId = open.StreamId }, timeout.Token);
                }
                catch (Exception ex) when (ex is PluginException or OperationCanceledException)
                {
                }
            }
        }
    }

    /// <summary>
    /// A logs call; what the target itself failed with (an unknown service, an option its
    /// platform can't honor) becomes a <see cref="ServiceLogsException"/>, as with a built-in target.
    /// </summary>
    private static async Task<T> LogsCallAsync<T>(PluginConnection connection, string method, object request, CancellationToken ct)
    {
        try
        {
            return await connection.InvokeAsync<T>(method, request, ct);
        }
        catch (PluginException ex) when (ex.InnerException is StreamJsonRpc.RemoteInvocationException
                                         {
                                             ErrorCode: PluginProtocol.ErrorCodes.TargetFailed
                                         } remote)
        {
            throw new ServiceLogsException(remote.Message, ex);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Stops the plugin process. The next call starts it again.</summary>
    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<PluginConnection> ConnectAsync(CancellationToken ct)
    {
        var connection = await _host.GetConnectionAsync(ct);
        if (_info is null)
        {
            _info = connection.Info.Targets.FirstOrDefault(t => DeploymentTargetType.TryParse(t.Id, out var id) && id == TargetType)
                ?? throw new PluginException(
                    $"Plugin '{connection.Name}' doesn't serve target '{TargetType}'. " +
                    $"It serves: {string.Join(", ", connection.Info.Targets.Select(t => t.Id))}.");
        }

        return connection;
    }

    /// <summary>
    /// Checks the environment's settings against the plugin's schema (once per config object) and
    /// sets the context the plugin's parameter requests are answered from.
    /// </summary>
    private void Prepare(AspireManifest? manifest, EnvironmentConfig config)
    {
        if (_info is { } info && !_checked.TryGetValue(config, out _))
        {
            var problems = PluginConfigCheck.Check(info, config);
            if (problems.Count > 0)
            {
                throw new PluginException(
                    $"The settings for target '{TargetType}' don't match what its plugin expects:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, problems.Select(p => $"  - {p}")));
            }

            _checked.AddOrUpdate(config, true);
        }

        _host.SetContext(manifest, config);
    }

    private async Task<T> CallAsync<T>(
        string method, AspireManifest? manifest, EnvironmentConfig config, string? outputDirectory, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        Prepare(manifest, config);
        return await connection.InvokeAsync<T>(method, new TargetRequest
        {
            TargetId = TargetType.Value,
            Manifest = manifest is null ? null : WireMapping.ToWire(manifest),
            Environment = WireMapping.ToWire(config),
            OutputDirectory = outputDirectory
        }, ct);
    }

    private async Task<T> ServiceCallAsync<T>(
        string method, AspireManifest? manifest, EnvironmentConfig config, string? outputDirectory,
        string service, string? revision, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        Prepare(manifest, config);
        return await connection.InvokeAsync<T>(method, new ServiceRequest
        {
            TargetId = TargetType.Value,
            Manifest = manifest is null ? null : WireMapping.ToWire(manifest),
            Environment = WireMapping.ToWire(config),
            OutputDirectory = outputDirectory,
            Service = service,
            Revision = revision
        }, ct);
    }

    private async Task<T> DeleteCallAsync<T>(
        string method, EnvironmentConfig config, string? service, DeleteOptions options, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct);
        Prepare(null, config);
        return await connection.InvokeAsync<T>(method, new DeleteRequest
        {
            TargetId = TargetType.Value,
            Environment = WireMapping.ToWire(config),
            Service = service,
            KeepSecrets = options.KeepSecrets,
            RemoveVolumes = options.RemoveVolumes
        }, ct);
    }
}
