using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record KickMemberCommand(Guid ConversationId, Guid UserId) : IRequest<Result>;

public sealed class KickMemberCommandValidator : AbstractValidator<KickMemberCommand>
{
    public KickMemberCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class KickMemberCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    ISignalRService signalR) : IRequestHandler<KickMemberCommand, Result>
{
    public async Task<Result> Handle(KickMemberCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        loaded.Value.KickMember(me, request.UserId);
        await db.SaveChangesAsync(cancellationToken);

        await signalR.RemoveFromConversationGroupAsync(request.UserId, request.ConversationId, cancellationToken);
        return Result.Success();
    }
}
