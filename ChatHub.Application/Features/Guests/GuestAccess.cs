using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Guests;

internal static class GuestAccess
{
    public static async Task<Error?> CheckContactsAsync(this IApplicationDbContext db, Guid actorId,
        IReadOnlyCollection<Guid> targetIds, CancellationToken cancellationToken)
    {
        if (targetIds.Count == 0) return null;

        var people = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == actorId || targetIds.Contains(u.Id))
            .Select(u => new { u.Id, u.IsGuest, u.IsDemoPersona })
            .ToListAsync(cancellationToken);
        var actorIsGuest = people.Any(p => p.Id == actorId && p.IsGuest);
        var targets = people.Where(p => p.Id != actorId).ToList();

        if (!actorIsGuest)
            return targets.Any(t => t.IsGuest) ? GuestErrors.NotReachable : null;

        var outsiders = targets.Where(t => !t.IsDemoPersona).Select(t => t.Id).ToList();
        if (outsiders.Count == 0) return null;

        var shared = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => outsiders.Contains(m.UserId) && m.IsActive && !m.Conversation.IsDeleted &&
                        m.Conversation.Members.Any(o => o.UserId == actorId && o.IsActive))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return outsiders.All(shared.Contains) ? null : GuestErrors.ContactRestricted;
    }

    public static Task<bool> IsGuestOrPersonaAsync(this IApplicationDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.UserProfiles.AnyAsync(u => u.Id == userId && (u.IsGuest || u.IsDemoPersona), cancellationToken);
}
