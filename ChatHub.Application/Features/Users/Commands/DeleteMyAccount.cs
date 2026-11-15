using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Files;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;
public sealed record DeleteMyAccountCommand : IRequest<Result>;

public sealed class DeleteMyAccountCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    AuthTokenService tokens,
    ISignalRService signalR) : IRequestHandler<DeleteMyAccountCommand, Result>
{
    public async Task<Result> Handle(DeleteMyAccountCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;

        var groups = await db.Conversations
            .Include(c => c.Members)
            .Where(c => c.Type == ConversationType.Group && !c.IsDeleted &&
                        c.Members.Any(m => m.UserId == userId && m.IsActive))
            .ToListAsync(cancellationToken);

        foreach (var group in groups)
            LeaveGroup(group, userId);

        await tokens.RevokeAllAsync(user.Id, cancellationToken);
        await db.MarkFileDeletedAsync(user.AvatarUrl, FilePurpose.Avatar, cancellationToken);
        user.Delete();
        await db.SaveChangesAsync(cancellationToken);

        await signalR.DisconnectUserAsync(userId, cancellationToken);

        foreach (var group in groups)
            await signalR.RemoveFromConversationGroupAsync(userId, group.Id, cancellationToken);

        return Result.Success();
    }

    private static void LeaveGroup(Conversation group, Guid userId)
    {
        var membership = group.FindActiveMember(userId)!;
        if (membership.Role == GroupRole.Owner)
        {
            var successor = group.Members
                .Where(m => m.IsActive && m.UserId != userId)
                .OrderByDescending(m => m.Role)
                .ThenBy(m => m.JoinedAtUtc)
                .ThenBy(m => m.Id)
                .FirstOrDefault();

            if (successor is not null)
                group.TransferOwnership(userId, successor.UserId);
        }

        group.LeaveMember(userId);
    }
}
