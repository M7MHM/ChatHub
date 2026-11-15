using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Messages.Commands;

public sealed record MarkAsReadCommand(Guid MessageId) : IRequest<Result>
{
    public Guid ConversationId { get; init; }
}

public sealed class MarkAsReadCommandValidator : AbstractValidator<MarkAsReadCommand>
{
    public MarkAsReadCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
    }
}

public sealed class MarkAsReadCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<MarkAsReadCommand, Result>
{
    public async Task<Result> Handle(MarkAsReadCommand request, CancellationToken cancellationToken)
    {
        var membership = await db.LoadMembershipAsync(request.ConversationId, currentUser.GetRequiredUserId(), cancellationToken);
        if (membership.IsFailure) return membership.Error;

        var message = await db.FindVisibleMessageAsync(membership.Value, request.MessageId, cancellationToken);
        if (message is null) return MessageErrors.NotFound;
        if (message.SenderId == membership.Value.UserId) return Result.Success();

        membership.Value.MarkAsRead(message);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
