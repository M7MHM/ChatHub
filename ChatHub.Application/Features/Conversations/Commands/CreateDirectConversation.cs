using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Entities;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record CreateDirectConversationCommand(Guid OtherUserId)
    : IRequest<Result<CreateDirectConversationResponse>>;

public sealed class CreateDirectConversationCommandValidator : AbstractValidator<CreateDirectConversationCommand>
{
    public CreateDirectConversationCommandValidator()
    {
        RuleFor(x => x.OtherUserId).NotEmpty();
    }
}

public sealed class CreateDirectConversationCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    ISignalRService signalR) : IRequestHandler<CreateDirectConversationCommand, Result<CreateDirectConversationResponse>>
{
    public async Task<Result<CreateDirectConversationResponse>> Handle(
        CreateDirectConversationCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var otherUserId = request.OtherUserId;
        if (otherUserId == me) return ConversationErrors.CannotChatWithSelf;

        var otherExists = await db.UserProfiles.AnyAsync(u => u.Id == otherUserId && !u.IsDeleted, cancellationToken);
        if (!otherExists) return UserErrors.NotFound;

        if (await db.IsBlockedEitherWayAsync(me, otherUserId, cancellationToken))
            return ConversationErrors.Blocked;

        var directKey = Conversation.BuildDirectKey(me, otherUserId);
        if (await FindByDirectKeyAsync(directKey, cancellationToken) is { } existingId)
            return new CreateDirectConversationResponse(existingId, Created: false);

        var conversation = Conversation.CreateDirect(me, otherUserId);
        db.Conversations.Add(conversation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Both users opened the chat at the same moment; the unique DirectKey index kept the other insert.
            var winnerId = await FindByDirectKeyAsync(directKey, cancellationToken);
            if (winnerId is null) throw;
            return new CreateDirectConversationResponse(winnerId.Value, Created: false);
        }

        await signalR.AddToConversationGroupAsync(me, conversation.Id, cancellationToken);
        await signalR.AddToConversationGroupAsync(otherUserId, conversation.Id, cancellationToken);
        return new CreateDirectConversationResponse(conversation.Id, Created: true);
    }

    private Task<Guid?> FindByDirectKeyAsync(string directKey, CancellationToken cancellationToken) =>
        db.Conversations
            .AsNoTracking()
            .Where(c => c.DirectKey == directKey)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed record CreateDirectConversationResponse(Guid ConversationId, bool Created);
