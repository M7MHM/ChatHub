using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record RefreshCommand(string RefreshToken) : IRequest<Result<AuthResponse>>;

public sealed class RefreshCommandValidator : AbstractValidator<RefreshCommand>
{
    public RefreshCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
    }
}

public sealed class RefreshCommandHandler(
    IApplicationDbContext db,
    IIdentityService identity,
    AuthTokenService tokens,
    TimeProvider timeProvider) : IRequestHandler<RefreshCommand, Result<AuthResponse>>
{
    private const int MaxSweepAttempts = 3;

    public async Task<Result<AuthResponse>> Handle(RefreshCommand request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (token is null) return AuthErrors.InvalidRefreshToken;

        if (token.ReplacedByTokenHash is not null)
        {
            await RevokeAllSessionsAsync(token.UserId, cancellationToken);
            return AuthErrors.TokenReused;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (!token.IsActive(nowUtc))
            return AuthErrors.InvalidRefreshToken;

        var user = await db.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == token.UserId && !u.IsDeleted &&
                                      (!u.IsGuest || u.GuestExpiresAtUtc > nowUtc), cancellationToken);
        if (user is null)
        {
            token.Revoke();
            await TrySaveAsync(cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        var response = tokens.Issue(user);
        token.Revoke(identity.HashToken(response.RefreshToken));

        return await TrySaveAsync(cancellationToken) ? response : AuthErrors.InvalidRefreshToken;
    }

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await tokens.RevokeAllAsync(userId, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxSweepAttempts)
            {
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(cancellationToken);
            }
        }
    }
}
