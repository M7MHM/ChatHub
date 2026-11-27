using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Guests;
using ChatHub.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Bot.Commands;

public sealed record SendBotReplyCommand(Guid TriggerMessageId) : IRequest<Result<bool>>;

public sealed class SendBotReplyCommandHandler(IApplicationDbContext db, TimeProvider timeProvider)
    : IRequestHandler<SendBotReplyCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(SendBotReplyCommand request, CancellationToken cancellationToken)
    {
        var job = await db.BotReplies.FirstOrDefaultAsync(r => r.Id == request.TriggerMessageId, cancellationToken);
        if (job is null || job.IsCompleted) return false;

        var botId = DemoPersonas.Bot.Id;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var conversation = await db.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == job.ConversationId && !c.IsDeleted, cancellationToken);
        var bot = conversation?.FindActiveMember(botId);

        var blocked = conversation is { Type: ConversationType.Direct } &&
                      await db.IsBlockedEitherWayAsync(botId, job.RequestedById, cancellationToken);
        if (conversation is null || bot is null || blocked || nowUtc - job.DueAtUtc > BotSettings.MaxLateness)
        {
            job.Abandon(nowUtc);
            await TrySaveAsync(cancellationToken);
            return false;
        }

        var trigger = await db.Messages.FirstOrDefaultAsync(m => m.Id == job.Id && !m.IsDeleted, cancellationToken);
        if (trigger is not null) bot.MarkAsRead(trigger);

        var previousReplies = await db.BotReplies.CountAsync(
            r => r.ConversationId == job.ConversationId && r.ReplyMessageId != null, cancellationToken);
        var reply = conversation.SendMessage(botId, MessageType.Text, BotReplyTexts.For(job.TriggerText, previousReplies));
        db.Messages.Add(reply);
        job.Complete(reply.Id, nowUtc);

        return await TrySaveAsync(cancellationToken);
    }

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
