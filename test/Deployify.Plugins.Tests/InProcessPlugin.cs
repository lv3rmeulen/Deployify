using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Models;
using Deployify.Plugin.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

namespace Deployify.Plugins.Tests;

/// <summary>
/// Runs a plugin built with the SDK in this process, connected to a <see cref="PluginHost"/>
/// over an in-memory duplex stream: the real server, the real proxy, no child process.
/// </summary>
internal sealed class InProcessPlugin : IAsyncDisposable
{
    private readonly List<(System.IO.Stream Stream, Task<int> Server)> _sessions = [];
    private readonly Action<PluginBuilder> _configure;

    public InProcessPlugin(Action<PluginBuilder> configure, ILoggerFactory? loggers = null, PluginLock? locked = null)
    {
        _configure = configure;
        Host = new PluginHost(
            new PluginLaunchSpec { ExecutablePath = "deployify-target-test", Source = "test", Lock = locked },
            loggers ?? NullLoggerFactory.Instance,
            new PluginHostOptions { HandshakeTimeout = TimeSpan.FromSeconds(10) },
            ConnectAsync);
    }

    public PluginHost Host { get; }

    /// <summary>How many times the host connected (more than one after a restart).</summary>
    public int Connections => _sessions.Count;

    public RemoteDeploymentTarget Target(string id) => new(Host, DeploymentTargetType.Parse(id));

    /// <summary>Drops the current connection from the plugin's side, as if the process died.</summary>
    public void Kill() => _sessions[^1].Stream.Dispose();

    private async Task<PluginConnection> ConnectAsync(PluginHost host, CancellationToken ct)
    {
        var (hostSide, pluginSide) = FullDuplexStream.CreatePair();
        var server = Task.Run(() => DeployifyPlugin.RunAsync(_configure, pluginSide, pluginSide, CancellationToken.None), CancellationToken.None);
        _sessions.Add((pluginSide, server));
        return await PluginConnection.ConnectAsync(
            host.Spec, hostSide, hostSide, host, host.Logger, host.Options.HandshakeTimeout, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        foreach (var (stream, server) in _sessions)
        {
            await stream.DisposeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
