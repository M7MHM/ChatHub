using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.Entities;

public class PasswordResetToken : BaseEntity
{
    private PasswordResetToken() { }

    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }

    public bool IsUsed => UsedAtUtc is not null;

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    internal static PasswordResetToken Issue(Guid userId, Email email, string tokenHash, DateTime nowUtc, TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
            throw new DomainRuleViolationException("User id is required.");
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainRuleViolationException("Token hash is required.");
        if (lifetime <= TimeSpan.Zero)
            throw new DomainRuleViolationException("A password reset token must have a positive lifetime.");

        return new PasswordResetToken
        {
            UserId = userId,
            Email = email.Value,
            TokenHash = tokenHash,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime
        };
    }

    public void Invalidate(DateTime nowUtc)
    {
        if (!IsUsed && ExpiresAtUtc > nowUtc) ExpiresAtUtc = nowUtc;
    }

    internal void MarkUsed(DateTime nowUtc)
    {
        if (IsUsed)
            throw new DomainRuleViolationException("The password reset token was already used.");
        if (IsExpired(nowUtc))
            throw new DomainRuleViolationException("The password reset token has expired.");
        UsedAtUtc = nowUtc;
    }
}
