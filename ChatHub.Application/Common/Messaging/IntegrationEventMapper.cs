using ChatHub.Application.Common.Emails;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Users;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Domain.Common;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using ContractConversationType = ChatHub.Contracts.Events.ConversationType;
using DomainConversationType = ChatHub.Domain.Enums.ConversationType;

namespace ChatHub.Application.Common.Messaging;

public static class IntegrationEventMapper
{
    public static async Task<IReadOnlyList<IIntegrationEvent>> ToIntegrationEventsAsync(this IApplicationDbContext db,
        IReadOnlyCollection<IDomainEvent> domainEvents, DateTime nowUtc, EmailLinks links, CancellationToken cancellationToken)
    {
        nowUtc = DatabasePrecision(nowUtc);
        List<IIntegrationEvent> integrationEvents =
        [
            .. await db.MessagesSentAsync(domainEvents.OfType<MessageSentEvent>().ToList(), cancellationToken),
            .. ConversationsRead(domainEvents.OfType<MessageReadEvent>()),
            .. db.UserChanges(domainEvents, nowUtc, links),
            .. db.EmailVerificationsRequested(domainEvents.OfType<EmailVerificationRequestedEvent>(), nowUtc, links),
            .. db.MessageChanges(domainEvents, nowUtc),
            .. MembershipChanges(domainEvents, nowUtc),
            .. PresenceChanges(domainEvents.OfType<UserStatusChangedEvent>(), nowUtc)
        ];
        return integrationEvents;
    }

    private static async Task<List<IIntegrationEvent>> MessagesSentAsync(this IApplicationDbContext db,
        IReadOnlyList<MessageSentEvent> events, CancellationToken cancellationToken)
    {
        var sent = events
            .Select(e => (Event: e, Message: db.Messages.Local.FirstOrDefault(m => m.Id == e.MessageId) ?? NotTracked<Message>(e.MessageId)))
            .Where(x => x.Message.Type != MessageType.System)
            .ToList();
        if (sent.Count == 0) return [];

        var conversationIds = sent.Select(x => x.Event.ConversationId).Distinct().ToList();
        var members = (await db.ConversationMembers
                .AsNoTracking()
                .Where(m => conversationIds.Contains(m.ConversationId))
                .Select(m => new MemberRow(m.ConversationId, m.Conversation.Type, m.Conversation.Title, m.UserId, m.IsActive,
                    m.IsMuted, m.User.DisplayName, m.User.IsDeleted))
                .ToListAsync(cancellationToken))
            .ToLookup(m => m.ConversationId);

        return sent.Select(x =>
        {
            var (e, message) = x;
            var conversationMembers = members[e.ConversationId].ToList();
            var sender = conversationMembers.FirstOrDefault(m => m.UserId == e.SenderId)
                ?? throw new InvalidOperationException(
                    $"Sender {e.SenderId} of message {e.MessageId} is not a member of conversation {e.ConversationId}.");
            var recipients = conversationMembers.Where(m => m.IsActive && !m.IsDeleted && m.UserId != e.SenderId).ToList();
            var sentAtUtc = DatabasePrecision(e.SentAtUtc);

            return (IIntegrationEvent)new MessageSent(
                Guid.CreateVersion7(),
                sentAtUtc,
                e.MessageId,
                e.ConversationId,
                sender.ConversationType == DomainConversationType.Group ? ContractConversationType.Group : ContractConversationType.Direct,
                sender.ConversationType == DomainConversationType.Group ? sender.ConversationTitle : null,
                e.SenderId,
                UserSummary.DisplayNameOf(sender.DisplayName, sender.IsDeleted),
                MessagePreview.SingleLine(message.Type, message.Content, MessageSent.MaxPreviewLength),
                sentAtUtc,
                [.. recipients.Select(m => m.UserId)],
                [.. recipients.Where(m => m.IsMuted).Select(m => m.UserId)]);
        }).ToList();
    }

    private static IEnumerable<IIntegrationEvent> ConversationsRead(IEnumerable<MessageReadEvent> events) =>
        events
            .GroupBy(e => (e.ConversationId, e.UserId))
            .Select(reads => reads.MaxBy(e => e.ReadUpToUtc)!)
            .Select(e => new ConversationRead(Guid.CreateVersion7(), DatabasePrecision(e.ReadAtUtc), e.ConversationId, e.UserId,
                DatabasePrecision(e.ReadUpToUtc)));

    private static IEnumerable<IIntegrationEvent> UserChanges(this IApplicationDbContext db,
        IReadOnlyCollection<IDomainEvent> events, DateTime nowUtc, EmailLinks links)
    {
        var unsubscribeLinks = events.OfType<UnsubscribeTokenIssuedEvent>()
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => links.Unsubscribe(g.Last().Token));

        var registered = events.OfType<UserRegisteredEvent>().Select(e => e.UserId).Distinct().ToList();
        foreach (var userId in registered)
        {
            var user = db.TrackedUser(userId);
            yield return new UserRegistered(Guid.CreateVersion7(), nowUtc, user.Id, user.Email?.Value, user.DisplayName,
                user.IsEmailVerified, user.EmailNotificationsEnabled, unsubscribeLinks.GetValueOrDefault(userId));
        }

        var updated = events
            .Select(e => e switch
            {
                UserProfileUpdatedEvent u => u.UserId,
                EmailVerifiedEvent v => v.UserId,
                EmailNotificationsChangedEvent n => n.UserId,
                UnsubscribeTokenIssuedEvent t => t.UserId,
                _ => (Guid?)null
            })
            .OfType<Guid>()
            .Distinct()
            .Except(registered);
        foreach (var userId in updated)
        {
            var user = db.TrackedUser(userId);
            yield return new UserProfileUpdated(Guid.CreateVersion7(), nowUtc, user.Id, user.Email?.Value, user.DisplayName,
                user.IsEmailVerified, user.EmailNotificationsEnabled, unsubscribeLinks.GetValueOrDefault(userId));
        }

        foreach (var userId in events.OfType<UserDeletedEvent>().Select(e => e.UserId).Distinct())
            yield return new UserDeleted(Guid.CreateVersion7(), nowUtc, userId);
    }

    private static IEnumerable<IIntegrationEvent> EmailVerificationsRequested(this IApplicationDbContext db,
        IEnumerable<EmailVerificationRequestedEvent> events, DateTime nowUtc, EmailLinks links) =>
        events.Select(e => new EmailVerificationRequested(Guid.CreateVersion7(), nowUtc, e.UserId, e.Email,
            db.TrackedUser(e.UserId).DisplayName, links.VerifyEmail(e.Token), DatabasePrecision(e.ExpiresAtUtc)));

    private static IEnumerable<IIntegrationEvent> MessageChanges(this IApplicationDbContext db,
        IReadOnlyCollection<IDomainEvent> events, DateTime nowUtc)
    {
        foreach (var messageId in events.OfType<MessageEditedEvent>().Select(e => e.MessageId).Distinct())
        {
            var message = db.Messages.Local.FirstOrDefault(m => m.Id == messageId) ?? NotTracked<Message>(messageId);
            if (message.Type == MessageType.System || message.IsDeleted) continue;
            yield return new MessageEdited(Guid.CreateVersion7(), nowUtc, message.Id, message.ConversationId,
                MessagePreview.SingleLine(message.Type, message.Content, MessageSent.MaxPreviewLength),
                DatabasePrecision(message.EditedAtUtc ?? nowUtc));
        }

        foreach (var deleted in events.OfType<MessageDeletedEvent>().DistinctBy(e => e.MessageId))
        {
            if (db.Messages.Local.FirstOrDefault(m => m.Id == deleted.MessageId) is { Type: MessageType.System }) continue;
            yield return new MessageDeleted(Guid.CreateVersion7(), nowUtc, deleted.MessageId, deleted.ConversationId);
        }
    }

    private static IEnumerable<IIntegrationEvent> MembershipChanges(IReadOnlyCollection<IDomainEvent> events, DateTime nowUtc)
    {
        foreach (var left in events.OfType<MemberLeftEvent>().DistinctBy(e => (e.ConversationId, e.UserId)))
            yield return new MemberLeft(Guid.CreateVersion7(), nowUtc, left.ConversationId, left.UserId);

        foreach (var kicked in events.OfType<MemberKickedEvent>().DistinctBy(e => (e.ConversationId, e.KickedUserId)))
            yield return new MemberKicked(Guid.CreateVersion7(), nowUtc, kicked.ConversationId, kicked.KickedUserId,
                kicked.KickedByUserId);

        foreach (var mute in events.OfType<MemberMuteChangedEvent>().GroupBy(e => (e.ConversationId, e.UserId)).Select(g => g.Last()))
            yield return new ConversationMuteChanged(Guid.CreateVersion7(), nowUtc, mute.ConversationId, mute.UserId, mute.IsMuted);
    }

    private static IEnumerable<IIntegrationEvent> PresenceChanges(IEnumerable<UserStatusChangedEvent> events, DateTime nowUtc) =>
        events.GroupBy(e => e.UserId)
            .Select(g => g.Last())
            .Select(e => new UserStatusChanged(Guid.CreateVersion7(), nowUtc, e.UserId, e.IsOnline));

    private static DateTime DatabasePrecision(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    private static UserProfile TrackedUser(this IApplicationDbContext db, Guid userId) =>
        db.UserProfiles.Local.FirstOrDefault(u => u.Id == userId) ?? NotTracked<UserProfile>(userId);

    private static T NotTracked<T>(Guid id) =>
        throw new InvalidOperationException($"{typeof(T).Name} {id} raised a domain event but is not tracked by the context.");

    private sealed record MemberRow(
        Guid ConversationId,
        DomainConversationType ConversationType,
        string? ConversationTitle,
        Guid UserId,
        bool IsActive,
        bool IsMuted,
        string DisplayName,
        bool IsDeleted);
}
