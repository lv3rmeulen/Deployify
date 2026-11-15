using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.ResourceSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deployify.ResourceServer;

/// <summary>How the resource server listens and who may connect.</summary>
public sealed class ResourceServerOptions
{
    /// <summary>
    /// Address to listen on: an IP address, <c>localhost</c>, or <c>*</c> / <c>0.0.0.0</c> for
    /// every interface. Default: 127.0.0.1, so only this machine can connect.
    /// </summary>
    public string BindAddress { get; init; } = "127.0.0.1";

    /// <summary>Port; 0 picks a free one (see <see cref="RunningResourceServer.Url"/>). Default: 18886.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// The key the dashboard must send (<c>Dashboard:ResourceServiceClient:AuthMode=ApiKey</c>).
    /// Null accepts every caller (<c>AuthMode=Unsecured</c>); only for a server bound to localhost.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>Shown as the application name in the dashboard's header.</summary>
    public string ApplicationName { get; init; } = "Deployify";

    public const int DefaultPort = 18886;

    /// <summary>A random 256-bit key, URL-safe.</summary>
    public static string GenerateApiKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// A standalone Aspire dashboard resource service: the gRPC endpoint that the dashboard's
/// <c>Dashboard:ResourceServiceClient:Url</c> points at, answered from any
/// <see cref="IResourceSource"/>. Serves plain HTTP/2 (h2c); put it behind TLS when it leaves the machine.
/// </summary>
public static class ResourceServer
{
    /// <summary>Starts listening and watching the source; dispose the result to stop.</summary>
    public static async Task<RunningResourceServer> StartAsync(
        IResourceSource source, ResourceServerOptions options, ILoggerFactory? loggers = null, CancellationToken ct = default)
    {
        loggers ??= NullLoggerFactory.Instance;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            void Http2(ListenOptions listen) => listen.Protocols = HttpProtocols.Http2;
            switch (options.BindAddress)
            {
                case "*" or "0.0.0.0" or "+":
                    kestrel.ListenAnyIP(options.Port, Http2);
                    break;
                case "localhost":
                    kestrel.ListenLocalhost(options.Port, Http2);
                    break;
                default:
                    kestrel.Listen(IPAddress.Parse(options.BindAddress), options.Port, Http2);
                    break;
            }
        });

        var hub = new ResourceHub(source, loggers.CreateLogger<ResourceHub>());
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(options);
        builder.Services.AddGrpc(grpc =>
        {
            grpc.EnableDetailedErrors = false;
            if (options.ApiKey is { } key)
            {
                grpc.Interceptors.Add<ApiKeyInterceptor>(key);
            }
        });

        var app = builder.Build();
        app.MapGrpcService<ResourceDashboardService>();
        hub.Start();
        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            await hub.DisposeAsync();
            await app.DisposeAsync();
            throw;
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                      ?? $"http://{options.BindAddress}:{options.Port}";
        return new RunningResourceServer(app, hub, new Uri(address.Replace("[::]", "localhost", StringComparison.Ordinal)));
    }
}

/// <summary>A started resource server.</summary>
public sealed class RunningResourceServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ResourceHub _hub;

    internal RunningResourceServer(WebApplication app, ResourceHub hub, Uri url)
    {
        _app = app;
        _hub = hub;
        Url = url;
    }

    /// <summary>The address it listens on, e.g. http://127.0.0.1:18886.</summary>
    public Uri Url { get; }

    /// <summary>Waits until the server stops (cancel <paramref name="ct"/> to stop it).</summary>
    public async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        finally
        {
            await _hub.DisposeAsync();
            await _app.DisposeAsync();
        }
    }
}
