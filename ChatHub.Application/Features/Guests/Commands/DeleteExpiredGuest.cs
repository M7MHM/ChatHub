using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Guests.Commands;

public sealed record DeleteExpiredGuestCommand(Guid UserId) : IRequest<Result<bool>>;

public sealed class DeleteExpiredGuestCommandHandler(
    IApplicationDbContext db,
    AccountDeletion deletion,
    TimeProvider timeProvider) : IRequestHandler<DeleteExpiredGuestCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(DeleteExpiredGuestCommand request, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var guestId = request.UserId;
        var expired = await db.UserProfiles.AnyAsync(
            u => u.Id == guestId && u.IsGuest && !u.IsDeleted && u.GuestExpiresAtUtc <= nowUtc, cancellationToken);
        if (!expired) return false;

        var conversationIds = await db.ConversationMembers
            .Where(m => m.UserId == guestId)
            .Select(m => m.ConversationId)
            .ToListAsync(cancellationToken);

        var deleted = await deletion.DeleteAsync(guestId, cancellationToken);
        if (deleted.IsFailure) return false;

        await db.Conversations
            .Where(c => conversationIds.Contains(c.Id) && !c.Members.Any(m => !m.User.IsGuest && !m.User.IsDemoPersona))
            .ExecuteDeleteAsync(cancellationToken);

        var files = await db.UploadedFiles
            .Where(f => f.OwnerId == guestId && !f.IsDeleted &&
                        !db.Messages.Any(m => m.Attachment != null && m.Attachment.FileId == f.Id))
            .ToListAsync(cancellationToken);
        foreach (var file in files)
            file.MarkDeleted();
        if (files.Count > 0) await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
