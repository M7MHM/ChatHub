using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Guests;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetUserProfileQuery(Guid UserId) : IRequest<Result<UserProfileResponse>>;

public sealed class GetUserProfileQueryValidator : AbstractValidator<GetUserProfileQuery>
{
    public GetUserProfileQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class GetUserProfileQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetUserProfileQuery, Result<UserProfileResponse>>
{
    public async Task<Result<UserProfileResponse>> Handle(GetUserProfileQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        if (await db.CheckContactsAsync(me, [request.UserId], cancellationToken) is not null) return UserErrors.NotFound;
        var iSharePresence = await db.SharesPresenceAsync(me, cancellationToken);

        var profile = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == request.UserId && !u.IsDeleted)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.AvatarUrl,
                u.Bio,
                u.IsOnline,
                u.LastSeenUtc,
                u.ShowLastSeen,
                IsBlockedByMe = db.UserBlocks.Any(b => b.BlockerId == me && b.BlockedId == u.Id),
                HasBlockedMe = db.UserBlocks.Any(b => b.BlockerId == u.Id && b.BlockedId == me)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null) return UserErrors.NotFound;

        var presenceVisible = profile.Id == me || (!profile.HasBlockedMe && profile.ShowLastSeen && iSharePresence);
        return new UserProfileResponse(
            profile.Id,
            profile.Username.Value,
            profile.DisplayName,
            profile.AvatarUrl,
            profile.Bio,
            presenceVisible && profile.IsOnline,
            presenceVisible ? profile.LastSeenUtc : null,
            profile.IsBlockedByMe);
    }
}

public sealed record UserProfileResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string? AvatarUrl,
    string? Bio,
    bool IsOnline,
    DateTime? LastSeenUtc,
    bool IsBlockedByMe);
