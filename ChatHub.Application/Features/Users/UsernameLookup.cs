using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users;

internal static class UsernameLookup
{
    public static Task<bool> IsUsernameTakenAsync(this IApplicationDbContext db, Username username, Guid? exceptUserId,
        CancellationToken cancellationToken) =>
        db.UserProfiles.AnyAsync(u => !u.IsDeleted && u.Username == username &&
                                      (exceptUserId == null || u.Id != exceptUserId.Value), cancellationToken);
}
