using ChatHub.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users;

internal static class UserPeers
{
    public static Task<List<Guid>> PeerIdsOfAsync(
        this IApplicationDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted)
            .SelectMany(m => m.Conversation.Members)
            .Where(o => o.IsActive && o.UserId != userId && !o.User.IsDeleted)
            .Where(o => !db.UserBlocks.Any(b =>
                (b.BlockerId == userId && b.BlockedId == o.UserId) || (b.BlockerId == o.UserId && b.BlockedId == userId)))
            .Select(o => o.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
