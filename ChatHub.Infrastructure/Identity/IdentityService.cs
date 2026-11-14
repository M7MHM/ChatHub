using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatHub.Infrastructure.Identity;

public class IdentityService(IOptions<JwtSettings> options, TimeProvider timeProvider) : IIdentityService
{
    private const int RefreshTokenSizeBytes = 64;
    private static readonly JsonWebTokenHandler TokenHandler = new();

    private readonly JwtSettings _settings = options.Value;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_settings.RefreshTokenDays);

    public (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(UserProfile user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = now.AddMinutes(_settings.ExpiryMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Secret));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAtUtc,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        return (TokenHandler.CreateToken(descriptor), expiresAtUtc);
    }

    public string GenerateRefreshToken() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenSizeBytes));

    public string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
