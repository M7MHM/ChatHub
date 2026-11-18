using Microsoft.Extensions.Logging;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed record LogEntry(string Category, string Message, Exception? Exception)
{
    public override string ToString() =>
        $"{Category}: {Message}{(Exception is null ? "" : $" | {Exception.GetType().Name}: {Exception.Message}")}";
}

public sealed class LogCapture : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _errors = [];

    public int Mark()
    {
        lock (_lock) return _errors.Count;
    }

    public IReadOnlyList<LogEntry> Since(int mark)
    {
        lock (_lock) return _errors.Skip(mark).ToList();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_lock) _errors.Add(entry);
    }

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) owner.Add(new LogEntry(category, formatter(state, exception), exception));
        }
    }
}
