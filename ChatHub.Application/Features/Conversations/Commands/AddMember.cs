using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Guests;
using ChatHub.Application.Features.Users;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record AddMemberCommand(Guid UserId) : IRequest<Result>
{
    public Guid ConversationId { get; init; }
}

public sealed class AddMemberCommandValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class AddMemberCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    ISignalRService signalR) : IRequestHandler<AddMemberCommand, Result>
{
    public async Task<Result> Handle(AddMemberCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        var targetExists = await db.UserProfiles
            .AnyAsync(u => u.Id == request.UserId && !u.IsDeleted, cancellationToken);
        if (!targetExists) return UserErrors.NotFound;

        if (await db.CheckContactsAsync(me, [request.UserId], cancellationToken) is { } restricted) return restricted;

        if (await db.IsBlockedEitherWayAsync(me, request.UserId, cancellationToken))
            return ConversationErrors.Blocked;

        loaded.Value.AddMember(me, request.UserId);
        await db.SaveChangesAsync(cancellationToken);

        await signalR.AddToConversationGroupAsync(request.UserId, request.ConversationId, cancellationToken);
        return Result.Success();
    }
}
