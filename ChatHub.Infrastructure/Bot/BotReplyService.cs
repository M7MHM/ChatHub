using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Bot;
using ChatHub.Application.Features.Bot.Commands;
using ChatHub.Infrastructure.Persistence.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Bot;

public sealed class BotReplyService(
    IServiceScopeFactory scopeFactory,
    IOptions<BotSettings> options,
    TimeProvider timeProvider,
    ILogger<BotReplyService> logger) : BackgroundService, IBotReplyWaker
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private readonly List<DateTime> _wakeTimes = [];
    private TaskCompletionSource _wake = NewSignal();
    private DateTime _nextCleanupUtc = DateTime.MinValue;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public void WakeAt(DateTime dueAtUtc)
    {
        lock (_gate)
        {
            _wakeTimes.Add(dueAtUtc);
            _wake.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken);
                await CleanUpIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sending bot replies failed; retrying on the next poll.");
            }

            try
            {
                await WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = UtcNow;
        var leasedUntilUtc = nowUtc + Lease;
        List<Guid> claimed;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            claimed = (await db.BotReplies.FromSql($"""
                    UPDATE "BotReplies" AS r SET "LockedUntilUtc" = {leasedUntilUtc}
                    FROM (
                        SELECT "Id" FROM "BotReplies"
                        WHERE "CompletedAtUtc" IS NULL AND "DueAtUtc" <= {nowUtc}
                          AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {nowUtc})
                        ORDER BY "DueAtUtc"
                        LIMIT {BatchSize}
                        FOR UPDATE SKIP LOCKED) AS due
                    WHERE r."Id" = due."Id"
                    RETURNING r.*, r.xmin
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken))
                .OrderBy(r => r.DueAtUtc)
                .Select(r => r.Id)
                .ToList();
        }

        foreach (var triggerMessageId in claimed)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new SendBotReplyCommand(triggerMessageId), cancellationToken);
        }

        return claimed.Count;
    }

    private async Task CleanUpIfDueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = UtcNow;
        if (nowUtc < _nextCleanupUtc) return;
        _nextCleanupUtc = nowUtc + CleanupInterval;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cutoff = nowUtc - Retention;
        await db.BotReplies.Where(r => r.RequestedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task signal;
        TimeSpan delay;
        lock (_gate)
        {
            var nowUtc = UtcNow;
            _wakeTimes.RemoveAll(t => t <= nowUtc);
            delay = options.Value.PollInterval;
            if (_wakeTimes.Count > 0 && _wakeTimes.Min() - nowUtc is var untilNext && untilNext < delay) delay = untilNext;
            if (_wake.Task.IsCompleted) _wake = NewSignal();
            signal = _wake.Task;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(signal, Task.Delay(delay, timeProvider, cancel.Token));
        await cancel.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
