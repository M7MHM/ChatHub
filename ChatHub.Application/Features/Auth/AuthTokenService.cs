using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth;

public sealed class AuthTokenService(IApplicationDbContext db, IIdentityService identity, TimeProvider timeProvider)
{
    public AuthResponse Issue(UserProfile user)
    {
        var (accessToken, accessTokenExpiresAtUtc) = identity.GenerateAccessToken(user);

        var refreshToken = identity.GenerateRefreshToken();
        var refreshTokenExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime + identity.RefreshTokenLifetime;
        db.RefreshTokens.Add(RefreshToken.Create(user.Id, identity.HashToken(refreshToken), refreshTokenExpiresAtUtc));

        return new AuthResponse(accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc,
            UserSummary.From(user));
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var activeTokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
            token.Revoke();
    }
}
