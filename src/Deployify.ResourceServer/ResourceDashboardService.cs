using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Aspire.DashboardService.Proto.V1;
using Deployify.Core.ResourceSources;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using ResourceCommandResult = Deployify.Core.ResourceSources.ResourceCommandResult;

namespace Deployify.ResourceServer;

/// <summary>
/// The dashboard's resource service, answered from an <see cref="IResourceSource"/>. Interactions,
/// file uploads and terminals stay unimplemented: the dashboard treats <c>Unimplemented</c> on
/// those as "not supported" and carries on.
/// </summary>
internal sealed class ResourceDashboardService(
    ResourceHub hub,
    IResourceSource source,
    ResourceServerOptions options,
    ILogger<ResourceDashboardService> logger) : DashboardService.DashboardServiceBase
{
    /// <summary>Same limit as Aspire's own service: a log update stays under 2M characters.</summary>
    internal const int LogMaxBatchCharacters = 2 * 1024 * 1024;

    private const int LogMaxBatchLines = 1000;

    public override Task<ApplicationInformationResponse> GetApplicationInformation(
        ApplicationInformationRequest request, ServerCallContext context)
        => Task.FromResult(new ApplicationInformationResponse { ApplicationName = options.ApplicationName });

    public override async Task WatchResources(
        WatchResourcesRequest request, IServerStreamWriter<WatchResourcesUpdate> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        try
        {
            using var subscription = await hub.SubscribeAsync(ct);
            var initial = new InitialResourceData();
            initial.Resources.AddRange(subscription.Initial.Select(ProtoMapper.ToProto));
            initial.ResourceTypes.AddRange(ProtoMapper.ResourceTypes(hub.Current));
            await responseStream.WriteAsync(new WatchResourcesUpdate { InitialData = initial }, ct);

            await foreach (var batch in subscription.Changes.ReadAllAsync(ct))
            {
                var changes = new WatchResourcesChanges();
                changes.Value.AddRange(batch.Select(ProtoMapper.ToProto));
                await responseStream.WriteAsync(new WatchResourcesUpdate { Changes = changes }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The dashboard disconnected.
        }
    }

    public override async Task WatchResourceConsoleLogs(
        WatchResourceConsoleLogsRequest request,
        IServerStreamWriter<WatchResourceConsoleLogsUpdate> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Lines are read into a channel and sent in batches: whatever has arrived when the
        // previous write finished goes in the next update.
        var lines = Channel.CreateBounded<ResourceLogLine>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in source.WatchLogsAsync(request.ResourceName, !request.SuppressFollow, cts.Token))
                {
                    await lines.Writer.WriteAsync(line, cts.Token);
                }

                lines.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Logs of {Resource} stopped: {Error}", request.ResourceName, ex.Message);
                lines.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var lineNumber = 0;
        try
        {
            while (await lines.Reader.WaitToReadAsync(ct))
            {
                var update = new WatchResourceConsoleLogsUpdate();
                var chars = 0;
                while (update.LogLines.Count < LogMaxBatchLines && chars < LogMaxBatchCharacters &&
                       lines.Reader.TryRead(out var line))
                {
                    var text = line.Text.Length > LogMaxBatchCharacters ? line.Text[..LogMaxBatchCharacters] : line.Text;
                    chars += text.Length;
                    update.LogLines.Add(new ConsoleLogLine { Text = text, IsStdErr = line.IsStdErr, LineNumber = ++lineNumber });
                }

                await responseStream.WriteAsync(update, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The dashboard closed the log view.
        }
        finally
        {
            await cts.CancelAsync();
            await reader;
        }
    }

    public override async Task<ResourceCommandResponse> ExecuteResourceCommand(
        ResourceCommandRequest request, ServerCallContext context)
    {
        ResourceCommandResult result;
        try
        {
            logger.LogInformation("Running '{Command}' on {Resource}.", request.CommandName, request.ResourceName);
            result = await source.ExecuteCommandAsync(request.ResourceName, request.CommandName, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return new ResourceCommandResponse { Kind = ResourceCommandResponseKind.Cancelled };
        }
        catch (Exception ex)
        {
            result = ResourceCommandResult.Failed(ex.Message);
        }
        finally
        {
            // Show the effect straight away instead of at the next poll.
            source.RequestRefresh();
        }

        if (!result.Success)
        {
            logger.LogWarning("'{Command}' on {Resource} failed: {Message}", request.CommandName, request.ResourceName, result.Message);
        }

        var response = new ResourceCommandResponse
        {
            Kind = result.Success ? ResourceCommandResponseKind.Succeeded : ResourceCommandResponseKind.Failed
        };
        if (result.Message is not null)
        {
            response.Message = result.Message;
        }

        return response;
    }
}
