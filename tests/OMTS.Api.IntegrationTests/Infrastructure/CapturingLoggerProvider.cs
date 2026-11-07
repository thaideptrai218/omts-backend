using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Keeps every formatted log line of the test host, to prove secrets never reach the logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
