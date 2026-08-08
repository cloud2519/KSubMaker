using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KSubMaker.UnitTests.Fakes;

public sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Minimal <see cref="ILogger{TCategoryName}"/> that keeps every formatted message, so a test can
/// assert on behaviour whose only externally visible effect is a log line.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    public IEnumerable<string> Messages => Records.Select(r => r.Message);

    public bool ContainsMessage(string fragment) =>
        Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _records.Enqueue(new LogRecord(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
