using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Messaging;

public interface IIntegrationEventProcessor
{
    Task<bool> ProcessAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

public sealed class IntegrationEventProcessor(
    NotificationsDbContext db,
    MessageSentHandler messages,
    ConversationReadHandler reads,
    UserEventsHandler users,
    EmailVerificationHandler verifications,
    ConversationEventsHandler conversations,
    PresenceHandler presence,
    TimeProvider timeProvider) : IIntegrationEventProcessor
{
    public async Task<bool> ProcessAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var first = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ProcessedMessages" ("MessageId", "ProcessedAtUtc") VALUES ({integrationEvent.MessageId}, {nowUtc})
            ON CONFLICT ("MessageId") DO NOTHING
            """, cancellationToken);
        if (first == 0) return false;

        var handled = integrationEvent switch
        {
            MessageSent e => messages.HandleAsync(e, nowUtc, cancellationToken),
            ConversationRead e => reads.HandleAsync(e, cancellationToken),
            UserRegistered e => users.UpsertAsync(new RecipientState(e.UserId, e.Email, e.DisplayName, e.EmailVerified,
                e.EmailNotificationsEnabled, e.UnsubscribeLink, e.OccurredAtUtc), cancellationToken),
            UserProfileUpdated e => users.UpsertAsync(new RecipientState(e.UserId, e.Email, e.DisplayName, e.EmailVerified,
                e.EmailNotificationsEnabled, e.UnsubscribeLink, e.OccurredAtUtc), cancellationToken),
            UserDeleted e => users.DeleteAsync(e.UserId, e.OccurredAtUtc, cancellationToken),
            EmailVerificationRequested e => verifications.HandleAsync(e, nowUtc, cancellationToken),
            MessageEdited e => conversations.MessageEditedAsync(e, nowUtc, cancellationToken),
            MessageDeleted e => conversations.MessageDeletedAsync(e, nowUtc, cancellationToken),
            MemberLeft e => conversations.MemberRemovedAsync(e.ConversationId, e.UserId, e.OccurredAtUtc, cancellationToken),
            MemberKicked e => conversations.MemberRemovedAsync(e.ConversationId, e.UserId, e.OccurredAtUtc, cancellationToken),
            ConversationMuteChanged e => conversations.MuteChangedAsync(e, cancellationToken),
            UserStatusChanged e => presence.HandleAsync(e, cancellationToken),
            _ => throw new NotSupportedException($"{integrationEvent.GetType().Name} has no handler.")
        };
        await handled;

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
