using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations;

internal static class ConversationAccess
{
    public static async Task<Result<Conversation>> LoadForMemberAsync(
        this IApplicationDbContext db, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == conversationId && !c.IsDeleted, cancellationToken);

        if (conversation is null) return ConversationErrors.NotFound;
        if (conversation.FindActiveMember(userId) is null) return ConversationErrors.NotMember;
        return conversation;
    }
    public static async Task<Result<ConversationMember>> LoadMembershipAsync(
        this IApplicationDbContext db, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var member = await db.ConversationMembers.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted,
            cancellationToken);
        if (member is not null) return member;

        var exists = await db.Conversations.AnyAsync(c => c.Id == conversationId && !c.IsDeleted, cancellationToken);
        return exists ? ConversationErrors.NotMember : ConversationErrors.NotFound;
    }

    public static Task<bool> IsBlockedEitherWayAsync(
        this IApplicationDbContext db, Guid userId, Guid otherUserId, CancellationToken cancellationToken) =>
        db.UserBlocks.AnyAsync(b =>
            (b.BlockerId == userId && b.BlockedId == otherUserId) ||
            (b.BlockerId == otherUserId && b.BlockedId == userId), cancellationToken);
}
