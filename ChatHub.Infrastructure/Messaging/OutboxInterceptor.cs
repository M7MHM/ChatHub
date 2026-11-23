using ChatHub.Application.Common.Emails;
using ChatHub.Application.Common.Messaging;
using ChatHub.Domain.Common;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChatHub.Infrastructure.Messaging;

internal sealed class OutboxInterceptor(TimeProvider timeProvider, EmailLinks links) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not ApplicationDbContext db) return result;

        var domainEvents = db.ChangeTracker.Entries<BaseEntity>()
            .SelectMany(e => e.Entity.DomainEvents)
            .ToList();
        if (domainEvents.Count == 0) return result;

        var integrationEvents = await db.ToIntegrationEventsAsync(domainEvents, timeProvider.GetUtcNow().UtcDateTime, links,
            cancellationToken);
        foreach (var integrationEvent in integrationEvents)
            db.OutboxMessages.Add(OutboxMessage.From(integrationEvent));

        return result;
    }
}
