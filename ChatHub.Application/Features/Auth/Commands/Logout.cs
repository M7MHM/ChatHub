using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record LogoutCommand(string RefreshToken) : IRequest<Result>;

public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

public sealed class LogoutCommandHandler(IApplicationDbContext db, IIdentityService identity)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token is { RevokedAtUtc: null })
        {
            token.Revoke();
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
