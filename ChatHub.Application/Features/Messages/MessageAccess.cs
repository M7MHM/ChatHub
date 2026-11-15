using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Conversations;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages;

internal static class MessageAccess
{
    public static Task<Message?> FindVisibleMessageAsync(
        this IApplicationDbContext db, ConversationMember member, Guid messageId, CancellationToken cancellationToken)
    {
        var visibleAfterUtc = MemberVisibility.VisibleAfterUtcOf(member);
        return db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == messageId && x.ConversationId == member.ConversationId &&
                                      x.CreatedAtUtc > visibleAfterUtc, cancellationToken);
    }
}
