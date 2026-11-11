using System;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Expressions;
using Deployify.Core.Models;
using Microsoft.Extensions.Logging;

namespace Deployify.Plugins;

/// <summary>Settings for running plugins.</summary>
public sealed class PluginHostOptions
{
    /// <summary>How long a plugin gets to start and answer the handshake.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// One plugin executable: starts it on first use, restarts it if it died, and answers its calls
/// back. Every <see cref="RemoteDeploymentTarget"/> of the plugin shares one host and so one process.
/// </summary>
public sealed class PluginHost : IPluginHostCallbacks, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private readonly PluginHostOptions _options;
    private readonly Func<PluginHost, CancellationToken, Task<PluginConnection>> _connect;
    private PluginConnection? _connection;
    private CallContext? _context;

    public PluginHost(PluginLaunchSpec spec, ILoggerFactory loggers, PluginHostOptions? options = null)
        : this(spec, loggers, options, connect: null)
    {
    }

    /// <param name="connect">
    /// Opens the connection; by default starts <see cref="PluginLaunchSpec.ExecutablePath"/>.
    /// Tests and in-process hosting pass their own.
    /// </param>
    public PluginHost(
        PluginLaunchSpec spec, ILoggerFactory loggers, PluginHostOptions? options,
        Func<PluginHost, CancellationToken, Task<PluginConnection>>? connect)
    {
        Spec = spec;
        _logger = loggers.CreateLogger($"Deployify.Plugins.{spec.DisplayName}");
        _options = options ?? new PluginHostOptions();
        _connect = connect ?? ((host, ct) => PluginConnection.StartAsync(spec, host, host._logger, host._options.HandshakeTimeout, ct));
    }

    /// <summary>The logger plugin output goes to.</summary>
    public ILogger Logger => _logger;

    /// <summary>Settings this host runs with.</summary>
    public PluginHostOptions Options => _options;

    public PluginLaunchSpec Spec { get; }

    /// <summary>The running connection, starting the plugin when it isn't running.</summary>
    public async Task<PluginConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is { IsAlive: true } running)
        {
            return running;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsAlive: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                _logger.LogWarning("Plugin {Plugin} stopped; starting it again.", _connection.Name);
                await _connection.DisposeAsync();
                _connection = null;
            }

            _connection = await _connect(this, ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Remembers the manifest and environment of the call being made, so parameter requests the
    /// plugin makes while handling it are answered from them. Calls without a manifest keep the
    /// last one.
    /// </summary>
    internal void SetContext(AspireManifest? manifest, EnvironmentConfig config)
        => _context = new CallContext(manifest ?? _context?.Manifest, config);

    public async Task<string?> ResolveParameterAsync(string parameter, string input, CancellationToken ct = default)
    {
        var context = _context
            ?? throw new InvalidOperationException("Parameter values can only be asked for while handling a call.");
        if (context.Manifest is null ||
            !context.Manifest.Resources.TryGetValue(parameter, out var resource) ||
            !string.Equals(resource.Type, "parameter.v0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{parameter}' is not a parameter in the manifest.");
        }

        if (!ParameterInput.ReadAll(resource).TryGetValue(input, out var declared))
        {
            if (input != "value")
            {
                throw new InvalidOperationException($"Parameter '{parameter}' has no input '{input}'.");
            }

            declared = new ParameterInput { Name = "value" };
        }

        var source = ParameterResolution.For(context.Config);
        var value = await source.GetValueAsync(parameter, declared, ct);
        _logger.LogDebug(
            "Plugin asked for parameter {Parameter}.{Input} ({Kind}); {Outcome}.",
            parameter, input, declared.Secret ? "secret" : "plain", value is null ? "no value" : "supplied");
        return value;
    }

    public void Log(LogLevel level, string? category, string message)
        => _logger.Log(level, "[{Plugin}{Category}] {Message}",
            _connection?.Name ?? Spec.DisplayName, category is null ? string.Empty : $"/{category}", message);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record CallContext(AspireManifest? Manifest, EnvironmentConfig Config);
}
