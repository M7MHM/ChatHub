using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record UnmuteConversationCommand(Guid ConversationId) : IRequest<Result>;

public sealed class UnmuteConversationCommandValidator : AbstractValidator<UnmuteConversationCommand>
{
    public UnmuteConversationCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class UnmuteConversationCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UnmuteConversationCommand, Result>
{
    public async Task<Result> Handle(UnmuteConversationCommand request, CancellationToken cancellationToken)
    {
        var membership = await db.LoadMembershipAsync(request.ConversationId, currentUser.GetRequiredUserId(), cancellationToken);
        if (membership.IsFailure) return membership.Error;

        membership.Value.Unmute();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
