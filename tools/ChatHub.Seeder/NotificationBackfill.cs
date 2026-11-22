using System.Diagnostics;
using ChatHub.Application.Common.Messaging;
using ChatHub.Domain.Common;
using ChatHub.Domain.Events;
using ChatHub.Infrastructure.Messaging;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Seeder;

public sealed record BackfillResult(int Users, int Batches, TimeSpan Elapsed);

public sealed class NotificationBackfill(ApplicationDbContext db, int batchSize, TextWriter output, TimeProvider? timeProvider = null)
{
    public async Task<BackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var clock = timeProvider ?? TimeProvider.System;
        var after = Guid.Empty;
        int users = 0, batches = 0;

        while (true)
        {
            var stampUtc = clock.GetUtcNow().UtcDateTime;
            var batch = await db.UserProfiles
                .FromSql($"""SELECT * FROM "UserProfiles" WHERE NOT "IsDeleted" AND "Id" > {after} ORDER BY "Id" LIMIT {batchSize}""")
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            var domainEvents = batch.Select(IDomainEvent (u) => new UserProfileUpdatedEvent(u.Id)).ToList();
            var integrationEvents = await db.ToIntegrationEventsAsync(domainEvents, stampUtc, cancellationToken);
            db.OutboxMessages.AddRange(integrationEvents.Select(OutboxMessage.From));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            after = batch[^1].Id;
            users += batch.Count;
            batches++;
            if (batch.Count < batchSize) break;
        }

        output.WriteLine($"Backfill: queued {users} UserProfileUpdated events in {batches} batches " +
                         $"({stopwatch.Elapsed.TotalSeconds:0.0} s); the API's outbox publisher delivers them.");
        return new BackfillResult(users, batches, stopwatch.Elapsed);
    }
}
