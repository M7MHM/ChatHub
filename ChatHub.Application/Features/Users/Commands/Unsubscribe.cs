using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record UnsubscribeCommand(string Token) : IRequest<Result>;

public sealed class UnsubscribeCommandValidator : AbstractValidator<UnsubscribeCommand>
{
    public const int MaxTokenLength = 256;

    public UnsubscribeCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(MaxTokenLength);
    }
}

public sealed class UnsubscribeCommandHandler(IApplicationDbContext db, IIdentityService identity)
    : IRequestHandler<UnsubscribeCommand, Result>
{
    public async Task<Result> Handle(UnsubscribeCommand request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.Token);
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.UnsubscribeTokenHash == tokenHash && !u.IsDeleted,
            cancellationToken);
        if (user is null) return UserErrors.InvalidUnsubscribeToken;

        user.SetEmailNotifications(false);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
