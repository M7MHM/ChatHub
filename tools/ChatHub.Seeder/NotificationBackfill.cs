using System.Diagnostics;
using ChatHub.Application.Common.Emails;
using ChatHub.Application.Common.Messaging;
using ChatHub.Domain.Common;
using ChatHub.Domain.Events;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Messaging;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Seeder;

public sealed record BackfillResult(int Users, int Batches, int UnsubscribeLinksIssued, TimeSpan Elapsed);

public sealed class NotificationBackfill(
    ApplicationDbContext db,
    int batchSize,
    TextWriter output,
    TimeProvider? timeProvider = null,
    EmailLinks? links = null,
    bool rotateUnsubscribeLinks = false)
{
    public async Task<BackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var clock = timeProvider ?? TimeProvider.System;
        var emailLinks = links ?? new EmailLinks(AppUrlSettings.DefaultPublicApiBaseUrl);
        var after = Guid.Empty;
        int users = 0, batches = 0, issued = 0;

        while (true)
        {
            var stampUtc = clock.GetUtcNow().UtcDateTime;
            var batch = await db.UserProfiles
                .FromSql($"""SELECT * FROM "UserProfiles" WHERE NOT "IsDeleted" AND "Id" > {after} ORDER BY "Id" LIMIT {batchSize}""")
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            foreach (var user in batch.Where(u => rotateUnsubscribeLinks || u.UnsubscribeTokenHash is null))
            {
                var token = OpaqueToken.Generate();
                user.IssueUnsubscribeToken(token, OpaqueToken.Hash(token));
                issued++;
            }

            List<IDomainEvent> domainEvents =
            [
                .. batch.Select(IDomainEvent (u) => new UserProfileUpdatedEvent(u.Id)),
                .. batch.SelectMany(u => u.DomainEvents)
            ];
            var integrationEvents = await db.ToIntegrationEventsAsync(domainEvents, stampUtc, emailLinks, cancellationToken);
            db.OutboxMessages.AddRange(integrationEvents.Select(OutboxMessage.From));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();

            after = batch[^1].Id;
            users += batch.Count;
            batches++;
            if (batch.Count < batchSize) break;
        }

        output.WriteLine($"Backfill: queued {users} UserProfileUpdated events in {batches} batches, " +
                         $"{issued} with a new unsubscribe link ({stopwatch.Elapsed.TotalSeconds:0.0} s); " +
                         "the API's outbox publisher delivers them.");
        return new BackfillResult(users, batches, issued, stopwatch.Elapsed);
    }
}
