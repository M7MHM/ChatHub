using ChatHub.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users;

internal sealed record Peer(Guid UserId, bool SharesPresence);

internal static class UserPeers
{
    public static async Task<List<Peer>> PeersOfAsync(
        this IApplicationDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var peers = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted)
            .SelectMany(m => m.Conversation.Members)
            .Where(o => o.IsActive && o.UserId != userId && !o.User.IsDeleted)
            .Where(o => !db.UserBlocks.Any(b =>
                (b.BlockerId == userId && b.BlockedId == o.UserId) || (b.BlockerId == o.UserId && b.BlockedId == userId)))
            .Select(o => new { o.UserId, o.User.ShowLastSeen })
            .Distinct()
            .ToListAsync(cancellationToken);
        return peers.Select(p => new Peer(p.UserId, p.ShowLastSeen)).ToList();
    }

    public static Task<bool> SharesPresenceAsync(
        this IApplicationDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => u.ShowLastSeen)
            .FirstOrDefaultAsync(cancellationToken);
}
