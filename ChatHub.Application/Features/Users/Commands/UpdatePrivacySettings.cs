using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record UpdatePrivacySettingsCommand(bool ShowLastSeen) : IRequest<Result>;

public sealed class UpdatePrivacySettingsCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UpdatePrivacySettingsCommand, Result>
{
    public async Task<Result> Handle(UpdatePrivacySettingsCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;

        user.SetLastSeenVisibility(request.ShowLastSeen);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
