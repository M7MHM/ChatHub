using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages.Commands;

public sealed record DeleteMessageCommand(Guid MessageId) : IRequest<Result>;

public sealed class DeleteMessageCommandValidator : AbstractValidator<DeleteMessageCommand>
{
    public DeleteMessageCommandValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
    }
}

public sealed class DeleteMessageCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<DeleteMessageCommand, Result>
{
    public async Task<Result> Handle(DeleteMessageCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var message = await db.Messages.FirstOrDefaultAsync(x => x.Id == request.MessageId, cancellationToken);
        if (message is null) return MessageErrors.NotFound;

        var membership = await db.LoadMembershipAsync(message.ConversationId, me, cancellationToken);
        if (membership.IsFailure) return membership.Error;

        message.Delete(me);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
