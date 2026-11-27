using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Guests;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Bot.EventHandlers;

public sealed class BotReplyScheduler(
    IApplicationDbContext db,
    ISignalRService signalR,
    IBotReplyWaker waker,
    IOptions<BotSettings> options,
    TimeProvider timeProvider,
    ILogger<BotReplyScheduler> logger) : DomainEventHandler<MessageSentEvent>(logger)
{
    protected override async Task HandleAsync(MessageSentEvent domainEvent, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var botId = DemoPersonas.Bot.Id;
        if (!settings.Enabled || domainEvent.SenderId == botId) return;

        var trigger = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id == domainEvent.MessageId)
            .Select(m => new
            {
                m.Type,
                m.Content,
                BotIsMember = !m.Conversation.IsDeleted && m.Conversation.Members.Any(x => x.UserId == botId && x.IsActive)
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (trigger is not { BotIsMember: true } || trigger.Type == MessageType.System) return;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var since = nowUtc - BotSettings.FloodWindow;
        var recent = await db.BotReplies
            .AsNoTracking()
            .Where(r => r.ConversationId == domainEvent.ConversationId && r.RequestedAtUtc > since)
            .Select(r => r.CompletedAtUtc)
            .ToListAsync(cancellationToken);
        if (recent.Any(completedAt => completedAt is null) || recent.Count >= settings.MaxRepliesPerConversationPerMinute)
            return;

        var reply = BotReply.Schedule(domainEvent.MessageId, domainEvent.ConversationId, domainEvent.SenderId, trigger.Content,
            nowUtc, settings.ReplyDelay);
        db.BotReplies.Add(reply);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return;
        }

        await signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.UserTyping,
            new UserTypingPayload(domainEvent.ConversationId, botId), cancellationToken);
        waker.WakeAt(reply.DueAtUtc);
    }
}
