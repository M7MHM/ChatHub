using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record UpdateEmailPreferencesCommand(bool EmailNotificationsEnabled) : IRequest<Result>;

public sealed class UpdateEmailPreferencesCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UpdateEmailPreferencesCommand, Result>
{
    public async Task<Result> Handle(UpdateEmailPreferencesCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;

        user.SetEmailNotifications(request.EmailNotificationsEnabled);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
