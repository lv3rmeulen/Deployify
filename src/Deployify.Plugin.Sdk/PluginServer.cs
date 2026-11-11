using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Interfaces;
using Deployify.Core.Models;
using Deployify.Plugin.Contracts;
using Deployify.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using Methods = Deployify.Plugin.Contracts.PluginProtocol.Methods;

namespace Deployify.Plugin.Sdk;

/// <summary>
/// Answers Deployify's calls by handing them to the registered targets. Every method yields
/// first, so a target whose code blocks never holds up the connection.
/// </summary>
internal sealed class PluginServer(
    PluginBuilder builder, IServiceProvider services, Func<JsonRpc> rpc, ILogger logger, Action onShutdown)
{
    private readonly Dictionary<DeploymentTargetType, PluginBuilder.TargetRegistration> _registrations =
        builder.Targets.ToDictionary(t => t.Id);

    // ── Session ──────────────────────────────────────────────────────────────

    [JsonRpcMethod(Methods.Initialize, UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeRequest request)
    {
        if (System.Version.TryParse(request.ProtocolVersion, out var host) && host.Major != PluginProtocol.Version.Major)
        {
            throw new LocalRpcException(
                $"This plugin speaks protocol {PluginProtocol.Version.ToString(2)}; Deployify speaks {request.ProtocolVersion}.")
            {
                ErrorCode = PluginProtocol.ErrorCodes.NotSupported
            };
        }

        logger.LogDebug("Handshake with Deployify {Version}.", request.HostVersion ?? "(unknown)");
        return new InitializeResult
        {
            ProtocolVersion = PluginProtocol.Version.ToString(2),
            Plugin = new PluginInfo { Name = builder.Name, Version = builder.Version },
            Targets = [.. builder.Targets.Select(t => t.ToInfo())]
        };
    }

    [JsonRpcMethod(Methods.Shutdown, UseSingleObjectParameterDeserialization = true)]
    public void Shutdown(object? _ = null) => onShutdown();

    // ── IDeploymentTarget ────────────────────────────────────────────────────

    [JsonRpcMethod(Methods.Ping, UseSingleObjectParameterDeserialization = true)]
    public Task<PingResult> PingAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, _, config) =>
        {
            var (ok, error) = await target.PingAsync(config, ct);
            return new PingResult(ok, error);
        });

    [JsonRpcMethod(Methods.Generate, UseSingleObjectParameterDeserialization = true)]
    public Task<GenerateResult> GenerateAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            new GenerateResult([.. await target.GenerateAsync(manifest, config, Output(request), ct)]));

    [JsonRpcMethod(Methods.Validate, UseSingleObjectParameterDeserialization = true)]
    public Task<ArtifactValidation[]> ValidateAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            (await target.ValidateAsync(manifest, config, Output(request), ct)).Select(WireMapping.ToWire).ToArray());

    [JsonRpcMethod(Methods.Plan, UseSingleObjectParameterDeserialization = true)]
    public Task<PlanInfo> PlanAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            WireMapping.ToWire(await target.PlanAsync(manifest, config, Output(request), ct)));

    [JsonRpcMethod(Methods.Apply, UseSingleObjectParameterDeserialization = true)]
    public Task<ServiceDeploymentInfo[]> ApplyAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            (await target.ApplyAsync(manifest, config, Output(request), ct)).Select(WireMapping.ToWire).ToArray());

    [JsonRpcMethod(Methods.Publish, UseSingleObjectParameterDeserialization = true)]
    public Task<object?> PublishAsync(PublishRequest request, CancellationToken ct)
        => RunAsync<object?>(request, async (target, manifest, config) =>
        {
            await target.PublishAsync(manifest, config, request.AppHostDirectory, ct);
            return null;
        });

    [JsonRpcMethod(Methods.Explain, UseSingleObjectParameterDeserialization = true)]
    public Task<ServiceExplanationInfo[]> ExplainAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            (await target.ExplainAsync(manifest, config, ct)).Select(WireMapping.ToWire).ToArray());

    [JsonRpcMethod(Methods.Dependencies, UseSingleObjectParameterDeserialization = true)]
    public Task<ServiceDependencyInfo[]> DependenciesAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            (await target.GetDependenciesAsync(manifest, config, ct)).Select(WireMapping.ToWire).ToArray());

    [JsonRpcMethod(Methods.Diff, UseSingleObjectParameterDeserialization = true)]
    public Task<DiffEntryInfo[]> DiffAsync(TargetRequest request, CancellationToken ct)
        => RunAsync(request, async (target, manifest, config) =>
            (await target.DiffAsync(manifest, config, Output(request), ct)).Select(WireMapping.ToWire).ToArray());

    // ── IRolloutTarget ───────────────────────────────────────────────────────

    [JsonRpcMethod(Methods.RolloutCapture, UseSingleObjectParameterDeserialization = true)]
    public Task<ServiceSnapshotInfo> CaptureAsync(ServiceRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, ServiceSnapshotInfo>(request, async (rollout, manifest, config) =>
            WireMapping.ToWire(await rollout.CaptureAsync(manifest, config, request.Service, ct)));

    [JsonRpcMethod(Methods.RolloutApplyService, UseSingleObjectParameterDeserialization = true)]
    public Task<AppliedRevisionInfo> ApplyServiceAsync(ServiceRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, AppliedRevisionInfo>(request, async (rollout, manifest, config) =>
            WireMapping.ToWire(await rollout.ApplyServiceAsync(manifest, config, Output(request), request.Service, ct)));

    [JsonRpcMethod(Methods.RolloutObserve, UseSingleObjectParameterDeserialization = true)]
    public Task<HealthObservationInfo> ObserveAsync(ServiceRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, HealthObservationInfo>(request, async (rollout, _, config) =>
            WireMapping.ToWire(await rollout.ObserveAsync(
                config, request.Service, request.Revision ?? throw Invalid("rollout/observe needs a revision."), ct)));

    [JsonRpcMethod(Methods.RolloutRestore, UseSingleObjectParameterDeserialization = true)]
    public Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, RestoreResult>(request, async (rollout, _, config) =>
            new RestoreResult(await rollout.RestoreAsync(config, WireMapping.FromWire(request.Snapshot), ct)));

    [JsonRpcMethod(Methods.RolloutNativeRollback, UseSingleObjectParameterDeserialization = true)]
    public Task<BoolResult> NativeRollbackAsync(TargetRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, BoolResult>(request, async (rollout, _, config) =>
            new BoolResult(await rollout.HasNativeRollbackAsync(config, ct)));

    [JsonRpcMethod(Methods.RolloutUnavailableReason, UseSingleObjectParameterDeserialization = true)]
    public Task<ReasonResult> UnavailableReasonAsync(TargetRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, ReasonResult>(request, async (rollout, _, config) =>
            new ReasonResult(await rollout.GetUnavailableReasonAsync(config, ct)));

    [JsonRpcMethod(Methods.RolloutComplete, UseSingleObjectParameterDeserialization = true)]
    public Task<object?> CompleteAsync(TargetRequest request, CancellationToken ct)
        => RunAsync<IRolloutTarget, object?>(request, async (rollout, manifest, config) =>
        {
            await rollout.CompleteAsync(manifest, config, Output(request), ct);
            return null;
        });

    // ── IResourceLifecycle ───────────────────────────────────────────────────

    [JsonRpcMethod(Methods.LifecycleListManaged, UseSingleObjectParameterDeserialization = true)]
    public Task<ManagedServiceInfo[]> ListManagedAsync(TargetRequest request, CancellationToken ct)
        => RunAsync<IResourceLifecycle, ManagedServiceInfo[]>(request, async (lifecycle, _, config) =>
            (await lifecycle.ListManagedAsync(config, ct)).Select(WireMapping.ToWire).ToArray());

    [JsonRpcMethod(Methods.LifecycleDeleteService, UseSingleObjectParameterDeserialization = true)]
    public Task<object?> DeleteServiceAsync(DeleteRequest request, CancellationToken ct)
        => RunAsync<IResourceLifecycle, object?>(request, async (lifecycle, _, config) =>
        {
            await lifecycle.DeleteServiceAsync(
                config, request.Service ?? throw Invalid("lifecycle/deleteService needs a service."),
                WireMapping.DeleteOptionsFromWire(request), ct);
            return null;
        });

    [JsonRpcMethod(Methods.LifecycleDestroyEnvironment, UseSingleObjectParameterDeserialization = true)]
    public Task<LinesResult> DestroyEnvironmentAsync(DeleteRequest request, CancellationToken ct)
        => RunAsync<IResourceLifecycle, LinesResult>(request, async (lifecycle, _, config) =>
            new LinesResult([.. await lifecycle.DestroyEnvironmentAsync(config, WireMapping.DeleteOptionsFromWire(request), ct)]));

    [JsonRpcMethod(Methods.LifecycleDescribeKept, UseSingleObjectParameterDeserialization = true)]
    public Task<LinesResult> DescribeKeptAsync(DeleteRequest request, CancellationToken ct)
        => RunAsync<IResourceLifecycle, LinesResult>(request, async (lifecycle, _, config) =>
            new LinesResult([.. await lifecycle.DescribeKeptAsync(config, WireMapping.DeleteOptionsFromWire(request), ct)]));

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private Task<T> RunAsync<T>(TargetRequest request, Func<IDeploymentTarget, AspireManifest, EnvironmentConfig, Task<T>> body)
        => RunAsync<IDeploymentTarget, T>(request, body);

    private async Task<T> RunAsync<TCapability, T>(
        TargetRequest request, Func<TCapability, AspireManifest, EnvironmentConfig, Task<T>> body)
        where TCapability : class
    {
        // Leave the connection's dispatch context before running target code.
        await Task.Yield();

        if (!DeploymentTargetType.TryParse(request.TargetId, out var id) || !_registrations.TryGetValue(id, out var registration))
        {
            throw new LocalRpcException($"This plugin doesn't serve target '{request.TargetId}'.")
            {
                ErrorCode = PluginProtocol.ErrorCodes.UnknownTarget
            };
        }

        var target = (IDeploymentTarget)services.GetRequiredService(registration.Type);
        if (target as TCapability is not { } capability)
        {
            throw new LocalRpcException($"Target '{registration.Id}' doesn't support {typeof(TCapability).Name}.")
            {
                ErrorCode = PluginProtocol.ErrorCodes.NotSupported
            };
        }

        var manifest = request.Manifest is { } m ? WireMapping.ManifestFromWire(m) : new AspireManifest();
        var config = WireMapping.EnvironmentFromWire(request.Environment);
        config.ParameterSource = new RemoteParameterSource(rpc());

        try
        {
            return await body(capability, manifest, config);
        }
        catch (Exception ex) when (ex is not LocalRpcException and not OperationCanceledException)
        {
            logger.LogDebug(ex, "Target {Target} failed.", registration.Id);
            // Only the message crosses: exception objects don't serialize reliably, and a stack
            // trace of the plugin's internals is of no use to the person running deploy.
            throw new LocalRpcException(ex.Message) { ErrorCode = PluginProtocol.ErrorCodes.TargetFailed };
        }
    }

    private static string Output(TargetRequest request)
        => request.OutputDirectory ?? throw Invalid("This call needs an output directory.");

    private static LocalRpcException Invalid(string message) => new(message) { ErrorCode = (int)StreamJsonRpc.Protocol.JsonRpcErrorCode.InvalidParams };
}

/// <summary>Asks Deployify for parameter values, one input at a time, as the target needs them.</summary>
internal sealed class RemoteParameterSource(JsonRpc rpc) : IParameterSource
{
    public async ValueTask<string?> GetValueAsync(string parameter, ParameterInput input, CancellationToken ct = default)
        => (await rpc.InvokeWithParameterObjectAsync<ParameterValueResult>(
                Methods.ResolveParameter, new ParameterValueRequest { Parameter = parameter, Input = input.Name }, ct))
            .Value;
}
