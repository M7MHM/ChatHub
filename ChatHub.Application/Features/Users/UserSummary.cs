using System.Linq.Expressions;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Application.Features.Users;

public sealed record UserSummary(
    Guid Id,
    string? Username,
    string DisplayName,
    string? AvatarUrl,
    bool IsOnline,
    DateTime? LastSeenUtc)
{
    public const string DeletedDisplayName = "Deleted Account";

    public static UserSummary Create(Guid id, Username username, string displayName, string? avatarUrl, bool isOnline,
        DateTime? lastSeenUtc, bool isDeleted, bool presenceVisible) =>
        isDeleted
            ? new UserSummary(id, null, DeletedDisplayName, null, false, null)
            : presenceVisible
                ? new UserSummary(id, username.Value, displayName, avatarUrl, isOnline, lastSeenUtc)
                : new UserSummary(id, username.Value, displayName, avatarUrl, false, null);

    public static string DisplayNameOf(string displayName, bool isDeleted) =>
        isDeleted ? DeletedDisplayName : displayName;

    public static Expression<Func<UserProfile, UserSummary>> ProjectionFor(Guid viewerId, bool viewerSharesPresence) =>
        u => Create(u.Id, u.Username, u.DisplayName, u.AvatarUrl, u.IsOnline, u.LastSeenUtc, u.IsDeleted,
            u.Id == viewerId || (u.ShowLastSeen && viewerSharesPresence));

    public static UserSummary From(UserProfile user) =>
        Create(user.Id, user.Username, user.DisplayName, user.AvatarUrl, user.IsOnline, user.LastSeenUtc, user.IsDeleted,
            presenceVisible: true);
}
