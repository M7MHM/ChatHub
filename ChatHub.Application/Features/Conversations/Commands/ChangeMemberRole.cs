using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record ChangeMemberRoleCommand(GroupRole Role) : IRequest<Result>
{
    public Guid ConversationId { get; init; }

    public Guid UserId { get; init; }
}

public sealed class ChangeMemberRoleCommandValidator : AbstractValidator<ChangeMemberRoleCommand>
{
    public ChangeMemberRoleCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Role).IsInEnum()
            .NotEqual(GroupRole.Owner).WithMessage("Use transfer ownership to assign the owner role.");
    }
}

public sealed class ChangeMemberRoleCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<ChangeMemberRoleCommand, Result>
{
    public async Task<Result> Handle(ChangeMemberRoleCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        loaded.Value.ChangeMemberRole(me, request.UserId, request.Role);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
