using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Deployify.Core.Interfaces;
using Deployify.Core.Processes;
using Deployify.Core.Services;
using Deployify.Plugin.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Deployify.Plugin.Sdk;

/// <summary>
/// Entry point of a plugin executable:
/// <code>
/// return await DeployifyPlugin.RunAsync(p => p
///     .AddTarget&lt;ExampleDeploymentTarget&gt;("example", "Example", t => t.WithConfigSchema&lt;ExampleTargetConfig&gt;()));
/// </code>
/// Name the executable <c>deployify-target-&lt;id&gt;</c> so Deployify finds it.
/// </summary>
public static class DeployifyPlugin
{
    /// <summary>
    /// Serves the protocol on stdin and stdout until Deployify disconnects. Anything the plugin
    /// writes to <see cref="Console.Out"/> goes to stderr instead, so it can't corrupt messages.
    /// Returns the process exit code.
    /// </summary>
    public static async Task<int> RunAsync(Action<PluginBuilder> configure, CancellationToken ct = default)
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        if (!Console.IsInputRedirected)
        {
            await Console.Error.WriteLineAsync(
                "This is a Deployify target plugin. Deployify starts it; it isn't meant to be run by hand.");
            return 2;
        }

        return await RunAsync(configure, input, output, ct);
    }

    /// <summary>Serves the protocol on the given streams. Used by tests and by hosts that start plugins in-process.</summary>
    public static async Task<int> RunAsync(Action<PluginBuilder> configure, Stream input, Stream output, CancellationToken ct = default)
    {
        var entry = Assembly.GetEntryAssembly()?.GetName();
        var builder = new PluginBuilder(entry?.Name ?? "plugin", entry?.Version?.ToString(3));

        JsonRpc rpc = null!;
        builder.Services
            .AddLogging(b => b
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new ForwardingLoggerProvider(() => rpc)))
            .AddSingleton<IProcessRunner>(ProcessRunner.Default)
            .AddSingleton<IPublisher, Publisher>();
        configure(builder);

        if (builder.Targets.Count == 0)
        {
            await Console.Error.WriteLineAsync("The plugin doesn't add any targets: call AddTarget in DeployifyPlugin.RunAsync.");
            return 2;
        }

        await using var services = builder.Services.BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Deployify.Plugin");

        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(PluginProtocol.JsonOptions)
        };
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, formatter));

        // "shutdown" only marks the session as ending: Deployify closes the stream next, which
        // ends the read loop cleanly. Disposing here would cut off that read loop mid-message.
        var shuttingDown = false;
        var server = new PluginServer(builder, services, () => rpc, logger, onShutdown: () => shuttingDown = true);
        rpc.AddLocalRpcTarget(server);
        rpc.StartListening();

        using (ct.Register(() => rpc.Dispose()))
        {
            try
            {
                await rpc.Completion;
            }
            catch (Exception ex) when (!shuttingDown && !ct.IsCancellationRequested &&
                                       ex is not (ConnectionLostException or ObjectDisposedException or IOException or OperationCanceledException))
            {
                await Console.Error.WriteLineAsync($"The connection to Deployify failed: {ex.Message}");
                return 1;
            }
            catch (Exception)
            {
                // Deployify went away or asked us to stop; that's how a session normally ends.
            }
        }

        return 0;
    }
}

/// <summary>Sends log lines to Deployify as <see cref="PluginProtocol.Methods.Log"/> notifications.</summary>
internal sealed class ForwardingLoggerProvider(Func<JsonRpc?> rpc) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ForwardingLogger(rpc, categoryName);

    public void Dispose()
    {
    }

    private sealed class ForwardingLogger(Func<JsonRpc?> rpc, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            var level = logLevel switch
            {
                LogLevel.Trace => LogLevelInfo.Trace,
                LogLevel.Debug => LogLevelInfo.Debug,
                LogLevel.Warning => LogLevelInfo.Warning,
                LogLevel.Error or LogLevel.Critical => LogLevelInfo.Error,
                _ => LogLevelInfo.Information
            };

            if (rpc() is { IsDisposed: false } connection)
            {
                // Fire and forget: a log line must never fail or slow down the target.
                _ = connection.NotifyWithParameterObjectAsync(PluginProtocol.Methods.Log, new LogMessage(level, message, category))
                    .ContinueWith(static _ => { }, TaskScheduler.Default);
            }
            else
            {
                Console.Error.WriteLine($"[{level}] {category}: {message}");
            }
        }
    }
}
