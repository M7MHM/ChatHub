using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class RefreshToken : BaseEntity
{
    private RefreshToken() { }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    public static RefreshToken Create(Guid userId, string tokenHash, DateTime expiresAtUtc)
    {
        if (userId == Guid.Empty)
            throw new DomainRuleViolationException("User id is required.");
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainRuleViolationException("Token hash is required.");

        var token = new RefreshToken { UserId = userId, TokenHash = tokenHash, ExpiresAtUtc = expiresAtUtc };
        if (token.ExpiresAtUtc <= token.CreatedAtUtc)
            throw new DomainRuleViolationException("Refresh token expiry must be in the future.");
        return token;
    }

    public void Revoke(string? replacedByTokenHash = null)
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = DateTime.UtcNow;
        ReplacedByTokenHash = string.IsNullOrWhiteSpace(replacedByTokenHash) ? null : replacedByTokenHash;
    }

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && !IsExpired(nowUtc);
}
