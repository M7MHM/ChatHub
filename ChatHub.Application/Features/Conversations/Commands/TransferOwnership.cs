using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record TransferOwnershipCommand(Guid NewOwnerId) : IRequest<Result>
{
    public Guid ConversationId { get; init; }
}

public sealed class TransferOwnershipCommandValidator : AbstractValidator<TransferOwnershipCommand>
{
    public TransferOwnershipCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.NewOwnerId).NotEmpty();
    }
}

public sealed class TransferOwnershipCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<TransferOwnershipCommand, Result>
{
    public async Task<Result> Handle(TransferOwnershipCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        loaded.Value.TransferOwnership(me, request.NewOwnerId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
