using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record ClearChatCommand(Guid ConversationId) : IRequest<Result>;

public sealed class ClearChatCommandValidator : AbstractValidator<ClearChatCommand>
{
    public ClearChatCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class ClearChatCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<ClearChatCommand, Result>
{
    public async Task<Result> Handle(ClearChatCommand request, CancellationToken cancellationToken)
    {
        var membership = await db.LoadMembershipAsync(request.ConversationId, currentUser.GetRequiredUserId(), cancellationToken);
        if (membership.IsFailure) return membership.Error;

        membership.Value.ClearChat();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
