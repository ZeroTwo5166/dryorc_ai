using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>One captured log call: category, level, formatted message, exception text (or null).</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, string? Exception);

/// <summary>
/// Records every log line of the host it is attached to, with its category,
/// level, formatted message and exception text. Shared by T-I3 and the
/// request-record redaction tests (D-4).
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    /// <summary>A snapshot; later log calls do not change it.</summary>
    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    /// <summary>Empties the store; a test calls it immediately before its act.</summary>
    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    /// <summary>A host derived from <paramref name="factory"/> whose logging also writes to <paramref name="provider"/>, every level included.</summary>
    internal static WebApplicationFactory<Program> Attach(WebApplicationFactory<Program> factory, CapturingLoggerProvider provider) =>
        factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(provider);
            l.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
        }));

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception?.ToString()));
    }
}
