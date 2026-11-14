using System.Linq.Expressions;
using ChatHub.Domain.Entities;

namespace ChatHub.Application.Features.Users;
public sealed record UserSummary(Guid Id, string DisplayName, string? AvatarUrl, bool IsOnline, DateTime? LastSeenUtc)
{
    public const string DeletedDisplayName = "Deleted Account";
    public static UserSummary Create(
        Guid id, string displayName, string? avatarUrl, bool isOnline, DateTime? lastSeenUtc, bool isDeleted) =>
        isDeleted
            ? new UserSummary(id, DeletedDisplayName, null, false, null)
            : new UserSummary(id, displayName, avatarUrl, isOnline, lastSeenUtc);

    public static string DisplayNameOf(string displayName, bool isDeleted) =>
        isDeleted ? DeletedDisplayName : displayName;
    public static readonly Expression<Func<UserProfile, UserSummary>> Projection =
        u => Create(u.Id, u.DisplayName, u.AvatarUrl, u.IsOnline, u.LastSeenUtc, u.IsDeleted);
    public static UserSummary From(UserProfile user) =>
        Create(user.Id, user.DisplayName, user.AvatarUrl, user.IsOnline, user.LastSeenUtc, user.IsDeleted);
}
