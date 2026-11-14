using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetMeQuery : IRequest<Result<MeResponse>>;

public sealed class GetMeQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMeQuery, Result<MeResponse>>
{
    public async Task<Result<MeResponse>> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();

        var me = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => new MeResponse(
                u.Id,
                u.DisplayName,
                u.Email == null ? null : u.Email.Value,
                u.PhoneNumber == null ? null : u.PhoneNumber.FullNumber,
                u.AvatarUrl,
                u.Bio,
                u.IsOnline,
                u.LastSeenUtc,
                u.CreatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return me is null ? UserErrors.NotFound : me;
    }
}

public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    string? AvatarUrl,
    string? Bio,
    bool IsOnline,
    DateTime? LastSeenUtc,
    DateTime CreatedAtUtc);
