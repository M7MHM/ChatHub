using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public sealed class LogCapture : ILoggerProvider
{
    public const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private readonly ConcurrentQueue<string> _errors = new();
    private int _sqlCommands;

    public int SqlCommands => Volatile.Read(ref _sqlCommands);

    public IReadOnlyList<string> Errors => [.. _errors];

    public void ResetSqlCommands() => Interlocked.Exchange(ref _sqlCommands, 0);

    public IReadOnlyList<string> TakeErrors()
    {
        var taken = new List<string>();
        while (_errors.TryDequeue(out var error)) taken.Add(error);
        return taken;
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error || category == CommandCategory;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == CommandCategory && eventId.Id == RelationalEventId.CommandExecuted.Id)
                Interlocked.Increment(ref owner._sqlCommands);

            if (logLevel >= LogLevel.Error && category.StartsWith("ChatHub.", StringComparison.Ordinal))
                owner._errors.Enqueue($"{category}: {formatter(state, exception)}" +
                                      (exception is null ? "" : $" | {exception.GetType().Name}: {exception.Message}"));
        }
    }
}
