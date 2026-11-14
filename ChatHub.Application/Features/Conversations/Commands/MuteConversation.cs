using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record MuteConversationCommand(Guid ConversationId) : IRequest<Result>;

public sealed class MuteConversationCommandValidator : AbstractValidator<MuteConversationCommand>
{
    public MuteConversationCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class MuteConversationCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<MuteConversationCommand, Result>
{
    public async Task<Result> Handle(MuteConversationCommand request, CancellationToken cancellationToken)
    {
        var membership = await db.LoadMembershipAsync(request.ConversationId, currentUser.GetRequiredUserId(), cancellationToken);
        if (membership.IsFailure) return membership.Error;

        membership.Value.Mute();
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
