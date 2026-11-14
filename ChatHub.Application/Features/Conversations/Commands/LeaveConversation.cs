using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record LeaveConversationCommand(Guid ConversationId) : IRequest<Result>;

public sealed class LeaveConversationCommandValidator : AbstractValidator<LeaveConversationCommand>
{
    public LeaveConversationCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class LeaveConversationCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    ISignalRService signalR) : IRequestHandler<LeaveConversationCommand, Result>
{
    public async Task<Result> Handle(LeaveConversationCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        loaded.Value.LeaveMember(me);
        await db.SaveChangesAsync(cancellationToken);

        await signalR.RemoveFromConversationGroupAsync(me, request.ConversationId, cancellationToken);
        return Result.Success();
    }
}
