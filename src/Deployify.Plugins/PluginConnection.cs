using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Plugin.Contracts;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Deployify.Plugins;

/// <summary>How to start a plugin.</summary>
public sealed record PluginLaunchSpec
{
    /// <summary>The executable, or a framework-dependent <c>.dll</c> that is started with <c>dotnet</c>.</summary>
    public required string ExecutablePath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Where the plugin was found, for messages ("PATH", "lock file", …).</summary>
    public string Source { get; init; } = "configured";

    /// <summary>The plugin name for messages before the handshake tells us its real one.</summary>
    public string DisplayName => Path.GetFileNameWithoutExtension(ExecutablePath);

    /// <summary>What the project's lock file requires of this plugin; null when it isn't locked.</summary>
    public PluginLock? Lock { get; init; }
}

/// <summary>A plugin failed to start, answer, or do what was asked. The message is meant for the user.</summary>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One running plugin process and the JSON-RPC connection to it. Starts the process, does the
/// handshake, answers the plugin's calls back (parameter values, log lines) and stops it again.
/// </summary>
public sealed class PluginConnection : IAsyncDisposable
{
    private const int StderrLinesKept = 40;

    private readonly PluginLaunchSpec _spec;
    private readonly ILogger _logger;
    private readonly Queue<string> _stderrTail = new();
    private readonly Process? _process;
    private readonly JsonRpc _rpc;
    private readonly Task _stderrPump;
    private int _disposed;

    private PluginConnection(
        PluginLaunchSpec spec, ILogger logger, Stream toPlugin, Stream fromPlugin, Process? process, IPluginHostCallbacks callbacks)
    {
        _spec = spec;
        _logger = logger;
        _process = process;

        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(PluginProtocol.JsonOptions)
        };
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(toPlugin, fromPlugin, formatter));
        _rpc.AddLocalRpcTarget(new HostEndpoint(callbacks));
        _rpc.StartListening();
        _stderrPump = process is null ? Task.CompletedTask : PumpStderrAsync(process.StandardError);
    }

    /// <summary>What the plugin said about itself in the handshake.</summary>
    public InitializeResult Info { get; private set; } = null!;

    public string Name => Info?.Plugin.Name ?? _spec.DisplayName;

    /// <summary>True until the process exits or the connection drops.</summary>
    public bool IsAlive => !_rpc.IsDisposed && !_rpc.Completion.IsCompleted && _process is not { HasExited: true };

    /// <summary>Starts the plugin and does the handshake. Throws <see cref="PluginException"/> when either fails.</summary>
    public static async Task<PluginConnection> StartAsync(
        PluginLaunchSpec spec, IPluginHostCallbacks callbacks, ILogger logger, TimeSpan handshakeTimeout,
        CancellationToken ct = default)
    {
        CheckLockedHash(spec);
        var process = new Process { StartInfo = CreateStartInfo(spec), EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            process.Dispose();
            throw new PluginException($"Could not start plugin '{spec.DisplayName}' ({spec.ExecutablePath}): {ex.Message}", ex);
        }

        var connection = new PluginConnection(
            spec, logger, process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process, callbacks);
        return await connection.HandshakeAsync(handshakeTimeout, ct);
    }

    /// <summary>Talks to a plugin over streams that are already open (a plugin hosted in-process, tests).</summary>
    public static Task<PluginConnection> ConnectAsync(
        PluginLaunchSpec spec, Stream toPlugin, Stream fromPlugin, IPluginHostCallbacks callbacks, ILogger logger,
        TimeSpan handshakeTimeout, CancellationToken ct = default)
        => new PluginConnection(spec, logger, toPlugin, fromPlugin, process: null, callbacks).HandshakeAsync(handshakeTimeout, ct);

    private async Task<PluginConnection> HandshakeAsync(TimeSpan handshakeTimeout, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(handshakeTimeout);
            try
            {
                Info = await InvokeAsync<InitializeResult>(
                    PluginProtocol.Methods.Initialize,
                    new InitializeRequest
                    {
                        ProtocolVersion = PluginProtocol.Version.ToString(2),
                        HostVersion = typeof(PluginConnection).Assembly.GetName().Version?.ToString(3)
                    },
                    timeout.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PluginException(DescribeExit(
                    $"Plugin '{_spec.DisplayName}' didn't answer the handshake within {handshakeTimeout.TotalSeconds:0}s."), ex);
            }

            CheckHandshake(_spec, Info);
            _logger.LogDebug(
                "Started plugin {Plugin} {Version} from {Path} ({Source}), protocol {Protocol}, targets {Targets}.",
                Info.Plugin.Name, Info.Plugin.Version, _spec.ExecutablePath, _spec.Source,
                Info.ProtocolVersion, string.Join(", ", Info.Targets.Select(t => t.Id)));
            return this;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Calls a plugin method with a single object parameter. Errors come back as
    /// <see cref="PluginException"/>; cancelling <paramref name="ct"/> cancels the call in the plugin too.
    /// </summary>
    public async Task<T> InvokeAsync<T>(string method, object request, CancellationToken ct = default)
    {
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<T>(method, request, ct);
        }
        catch (ConnectionLostException ex)
        {
            throw new PluginException(DescribeExit($"Plugin '{Name}' stopped while handling {method}."), ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new PluginException(DescribeExit($"Plugin '{Name}' is no longer running."), ex);
        }
        catch (RemoteRpcException ex) when (ex is not ConnectionLostException)
        {
            // An error the plugin returned, or one it couldn't serialize.
            throw new PluginException($"{Name}: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (IsAlive)
            {
                await _rpc.NotifyWithParameterObjectAsync(PluginProtocol.Methods.Shutdown, new { });
            }
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or IOException)
        {
            // Already gone.
        }

        _rpc.Dispose();
        if (_process is null)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        if (!_process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Plugin {Plugin} didn't exit after shutdown; stopping it.", Name);
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await _stderrPump.ConfigureAwait(false);
        _process.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(PluginLaunchSpec spec)
    {
        var dll = spec.ExecutablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(dll ? "dotnet" : spec.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(spec.ExecutablePath)) ?? Environment.CurrentDirectory
        };

        if (dll)
        {
            info.ArgumentList.Add(spec.ExecutablePath);
        }

        foreach (var arg in spec.Arguments)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["DEPLOYIFY_PLUGIN"] = "1";
        info.Environment["DEPLOYIFY_PLUGIN_PROTOCOL"] = PluginProtocol.Version.ToString(2);
        return info;
    }

    private static void CheckLockedHash(PluginLaunchSpec spec)
    {
        if (spec.Lock?.Sha256 is not { } expected || !File.Exists(spec.ExecutablePath))
        {
            return;
        }

        var actual = PluginLockFile.HashFile(spec.ExecutablePath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(
                $"Plugin '{spec.DisplayName}' at {spec.ExecutablePath} isn't the file {PluginLockFile.FileName} locked " +
                $"(SHA-256 {actual[..12]}…, locked {expected[..Math.Min(12, expected.Length)]}…). " +
                "If you updated it on purpose, run 'deployify plugin lock' and commit the lock file.");
        }
    }

    private static void CheckHandshake(PluginLaunchSpec spec, InitializeResult info)
    {
        if (spec.Lock is { Version: { } lockedVersion } &&
            !string.Equals(lockedVersion, info.Plugin.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(
                $"Plugin '{info.Plugin.Name}' is version {info.Plugin.Version ?? "(none)"}, but {PluginLockFile.FileName} " +
                $"locks version {lockedVersion}. Install that version, or run 'deployify plugin lock' to accept this one.");
        }

        if (!Version.TryParse(info.ProtocolVersion, out var version))
        {
            throw new PluginException($"Plugin '{spec.DisplayName}' sent an unreadable protocol version '{info.ProtocolVersion}'.");
        }

        if (version.Major != PluginProtocol.Version.Major)
        {
            throw new PluginException(
                $"Plugin '{info.Plugin.Name}' speaks protocol {version.ToString(2)}, but this Deployify speaks " +
                $"{PluginProtocol.Version.ToString(2)}. Install a plugin version built for protocol {PluginProtocol.Version.Major}.x.");
        }

        if (info.Targets.Count == 0)
        {
            throw new PluginException($"Plugin '{info.Plugin.Name}' doesn't serve any targets.");
        }
    }

    private async Task PumpStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync() is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrLinesKept)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                _logger.LogDebug("[plugin:{Plugin}] {Line}", Name, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private string DescribeExit(string headline)
    {
        // A plugin that just died may not have finished exiting, nor its last stderr lines been
        // read. This is the error path, so waiting a moment for both is worth the better message.
        if (_process is not null)
        {
            try
            {
                if (_process.WaitForExit(TimeSpan.FromSeconds(2)))
                {
                    _stderrPump.Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
            {
            }
        }

        var sb = new StringBuilder(headline);
        if (_process is { HasExited: true })
        {
            sb.Append($" It exited with code {_process.ExitCode}.");
        }

        string[] tail;
        lock (_stderrTail)
        {
            tail = [.. _stderrTail];
        }

        if (tail.Length > 0)
        {
            sb.AppendLine(" Last output:");
            foreach (var line in tail.TakeLast(15))
            {
                sb.Append("  ").AppendLine(line);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Methods the plugin may call on Deployify.</summary>
    private sealed class HostEndpoint(IPluginHostCallbacks callbacks)
    {
        [JsonRpcMethod(PluginProtocol.Methods.ResolveParameter, UseSingleObjectParameterDeserialization = true)]
        public async Task<ParameterValueResult> ResolveParameterAsync(ParameterValueRequest request, CancellationToken ct)
            => new(await callbacks.ResolveParameterAsync(request.Parameter, request.Input, ct));

        [JsonRpcMethod(PluginProtocol.Methods.Log, UseSingleObjectParameterDeserialization = true)]
        public void Log(LogMessage message)
        {
            var level = message.Level switch
            {
                LogLevelInfo.Trace => LogLevel.Trace,
                LogLevelInfo.Debug => LogLevel.Debug,
                LogLevelInfo.Warning => LogLevel.Warning,
                LogLevelInfo.Error => LogLevel.Error,
                _ => LogLevel.Information
            };
            callbacks.Log(level, message.Category, message.Message);
        }
    }
}

/// <summary>What Deployify does when a plugin calls back.</summary>
public interface IPluginHostCallbacks
{
    /// <summary>The value of one parameter input, or null. Throws when the plugin may not have it.</summary>
    Task<string?> ResolveParameterAsync(string parameter, string input, CancellationToken ct = default);

    void Log(LogLevel level, string? category, string message);
}
