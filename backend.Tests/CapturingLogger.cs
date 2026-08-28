using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>
/// Step 21 Finding A remediation tests. Minimal in-memory ILogger&lt;T&gt;
/// double — records every Log call so a test can assert a failure was
/// actually logged, without pulling in a logging test framework.
/// </summary>
public class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
