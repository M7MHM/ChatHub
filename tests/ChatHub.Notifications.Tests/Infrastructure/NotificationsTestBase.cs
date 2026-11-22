using System.Collections.Concurrent;
using System.Data.Common;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Messaging;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ChatHub.Notifications.Tests.Infrastructure;

[Collection(NotificationsCollection.Name)]
public abstract class NotificationsTestBase(NotificationsFixture fixture) : IAsyncLifetime
{
    protected static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    protected static readonly TimeSpan Delay = TimeSpan.FromSeconds(120);
    protected static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private ServiceProvider? _services;

    protected NotificationsFixture Fixture { get; } = fixture;
    protected FakeTimeProvider Clock { get; } = new(Start);
    protected FakeChannel Channel { get; } = new();
    protected CommandCounter Commands { get; } = new();
    protected ErrorLog Errors { get; } = new();

    protected IServiceProvider Services => _services ?? throw new InvalidOperationException("Services are not built yet.");

    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;

    protected virtual IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>();

    public async Task InitializeAsync()
    {
        await Fixture.ResetAsync();
        _services = BuildServices();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        Errors.Entries.ShouldBeEmpty("the notifications service logged unexpected errors");
    }

    public static Dictionary<string, string?> BaseSettings(NotificationsFixture fixture) => new()
    {
        ["ConnectionStrings:Notifications"] = fixture.ConnectionString,
        ["RabbitMq:Host"] = fixture.Rabbit.Hostname,
        ["RabbitMq:Port"] = fixture.RabbitPort.ToString(),
        ["RabbitMq:User"] = NotificationsFixture.RabbitUser,
        ["RabbitMq:Password"] = NotificationsFixture.RabbitPassword,
        ["RabbitMq:MaxDeliveryAttempts"] = "3",
        ["RabbitMq:RetryDelayMilliseconds"] = "200",
        ["Smtp:Host"] = "localhost",
        ["Smtp:Port"] = "1025",
        ["Notifications:DigestDelaySeconds"] = "120",
        ["Notifications:CooldownMinutes"] = "15",
        ["Notifications:AppBaseUrl"] = "https://chat.example.test/",
        ["Notifications:MaxAttempts"] = "3",
        ["Notifications:RetryBaseDelaySeconds"] = "30",
        ["Notifications:BatchSize"] = "20"
    };

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected async Task<bool> ProcessAsync(IIntegrationEvent integrationEvent)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>()
            .ProcessAsync(integrationEvent, CancellationToken.None);
    }

    protected Task<int> DispatchAsync() => Services.GetRequiredService<DigestDispatcher>().DispatchDueAsync(CancellationToken.None);

    protected async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    protected Task<PendingDigest?> DigestAsync(Guid userId, Guid conversationId) =>
        QueryAsync(db => db.PendingDigests.AsNoTracking().SingleOrDefaultAsync(d => d.UserId == userId && d.ConversationId == conversationId));

    protected Task<List<PendingDigest>> DigestsAsync() => QueryAsync(db => db.PendingDigests.AsNoTracking().ToListAsync());

    protected async Task<Guid> UserAsync(string name, bool withEmail = true)
    {
        var id = Guid.CreateVersion7();
        (await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), Now, id,
            withEmail ? $"{name.ToLowerInvariant()}@example.com" : null, name))).ShouldBeTrue();
        return id;
    }

    protected MessageSent Message(Guid conversationId, Guid senderId, string senderName, IReadOnlyList<Guid> recipients,
        string? preview = "hello", ConversationType type = ConversationType.Direct, string? title = null,
        IReadOnlyList<Guid>? muted = null, DateTime? sentAtUtc = null)
    {
        var at = sentAtUtc ?? Now;
        return new MessageSent(Guid.CreateVersion7(), at, Guid.CreateVersion7(), conversationId, type, title, senderId, senderName,
            preview, at, recipients, muted ?? []);
    }

    protected ConversationRead Read(Guid userId, Guid conversationId, DateTime readUpToUtc) =>
        new(Guid.CreateVersion7(), Now, conversationId, userId, readUpToUtc);

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(BaseSettings(Fixture))
            .AddInMemoryCollection(Settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning).AddProvider(Errors));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddNotificationsService(configuration);
        services.RemoveAll<INotificationChannel>();
        services.AddSingleton<INotificationChannel>(Channel);
        services.ConfigureDbContext<NotificationsDbContext>(options => options.AddInterceptors(Commands));
        ConfigureServices(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

public sealed class FakeChannel : INotificationChannel
{
    private readonly ConcurrentQueue<DigestNotification> _sent = new();
    private int _failuresLeft;

    public string Name => "fake";

    public bool FailAlways { get; set; }

    public Action? OnSend { get; set; }

    public IReadOnlyList<DigestNotification> Sent => [.. _sent];

    public void FailNext(int times) => Interlocked.Exchange(ref _failuresLeft, times);

    public bool CanDeliverTo(Recipient recipient) => !recipient.IsDeleted && !string.IsNullOrWhiteSpace(recipient.Email);

    public Task SendAsync(DigestNotification notification, CancellationToken cancellationToken)
    {
        if (FailAlways || Interlocked.Decrement(ref _failuresLeft) >= 0)
            throw new IOException("SMTP relay unavailable");
        OnSend?.Invoke();
        _sent.Enqueue(notification);
        return Task.CompletedTask;
    }
}

public sealed class CommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class ErrorLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public IReadOnlyList<string> Take()
    {
        var taken = new List<string>();
        while (_entries.TryDequeue(out var entry)) taken.Add(entry);
        return taken;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ErrorLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && category.StartsWith("ChatHub.", StringComparison.Ordinal))
                owner._entries.Enqueue($"{category}: {formatter(state, exception)}");
        }
    }
}
