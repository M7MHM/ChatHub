using ChatHub.Domain.Entities;

namespace ChatHub.Application.Common.Interfaces;

public interface IIdentityService
{
    TimeSpan RefreshTokenLifetime { get; }

    (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(UserProfile user);
    string GenerateRefreshToken();
    string HashToken(string token);
}
