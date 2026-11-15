using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Conversations;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages;

internal static class MessageAccess
{
    public static IQueryable<Message> VisibleTo(this IQueryable<Message> messages, ConversationMember member)
    {
        var visibleAfterUtc = MemberVisibility.VisibleAfterUtcOf(member);
        return messages.Where(x => x.ConversationId == member.ConversationId && x.CreatedAtUtc > visibleAfterUtc);
    }

    public static Task<Message?> FindVisibleMessageAsync(
        this IApplicationDbContext db, ConversationMember member, Guid messageId, CancellationToken cancellationToken) =>
        db.Messages
            .AsNoTracking()
            .VisibleTo(member)
            .FirstOrDefaultAsync(x => x.Id == messageId, cancellationToken);
}
