using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Deployify.Hosting.Tests;

/// <summary>Keeps every formatted log message (with its exception) for assertions.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
