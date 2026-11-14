using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetMyBlockedUsersQuery : IRequest<Result<IReadOnlyList<BlockedUserResponse>>>;

public sealed class GetMyBlockedUsersQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMyBlockedUsersQuery, Result<IReadOnlyList<BlockedUserResponse>>>
{
    public async Task<Result<IReadOnlyList<BlockedUserResponse>>> Handle(
        GetMyBlockedUsersQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var blocked = await db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockerId == me)
            .OrderByDescending(b => b.BlockedAtUtc)
            .Select(b => new BlockedUserResponse(
                UserSummary.Create(b.Blocked.Id, b.Blocked.DisplayName, b.Blocked.AvatarUrl, b.Blocked.IsOnline,
                    b.Blocked.LastSeenUtc, b.Blocked.IsDeleted),
                b.BlockedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<BlockedUserResponse>>(blocked);
    }
}

public sealed record BlockedUserResponse(UserSummary User, DateTime BlockedAtUtc);
