using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;
public sealed record UnblockUserCommand(Guid UserId) : IRequest<Result>;

public sealed class UnblockUserCommandValidator : AbstractValidator<UnblockUserCommand>
{
    public UnblockUserCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public sealed class UnblockUserCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UnblockUserCommand, Result>
{
    public async Task<Result> Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var block = await db.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == me && b.BlockedId == request.UserId, cancellationToken);

        if (block is not null)
        {
            db.UserBlocks.Remove(block);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
