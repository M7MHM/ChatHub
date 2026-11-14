using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Entities;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;
public sealed record BlockUserCommand(Guid UserId) : IRequest<Result>;

public sealed class BlockUserCommandValidator : AbstractValidator<BlockUserCommand>
{
    public BlockUserCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class BlockUserCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<BlockUserCommand, Result>
{
    public async Task<Result> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        if (request.UserId == me) return UserErrors.CannotBlockSelf;

        var targetExists = await db.UserProfiles
            .AnyAsync(u => u.Id == request.UserId && !u.IsDeleted, cancellationToken);
        if (!targetExists) return UserErrors.NotFound;

        var alreadyBlocked = await db.UserBlocks
            .AnyAsync(b => b.BlockerId == me && b.BlockedId == request.UserId, cancellationToken);
        if (alreadyBlocked) return Result.Success();

        db.UserBlocks.Add(UserBlock.Create(me, request.UserId));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
