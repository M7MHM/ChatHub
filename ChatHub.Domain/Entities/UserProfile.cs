using ChatHub.Domain.Common;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.Entities;

public class UserProfile : BaseAuditableEntity
{
    public const int MaxDisplayNameLength = 100;
    public const int MaxBioLength = 500;

    private UserProfile() { }

    public PhoneNumber? PhoneNumber { get; private set; }
    public Email? Email { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public Guid SecurityStamp { get; private set; }
    public string? AvatarUrl { get; private set; }
    public string? Bio { get; private set; }
    public bool IsOnline { get; private set; } = false;
    public DateTime? LastSeenUtc { get; private set; }
    public DateTime? EmailVerifiedAtUtc { get; private set; }
    public bool EmailNotificationsEnabled { get; private set; } = true;
    public string? UnsubscribeTokenHash { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAtUtc is not null;

    public ICollection<ConversationMember> Memberships { get; private set; } = new List<ConversationMember>();
    public ICollection<UserBlock> BlockedUsers { get; private set; } = new List<UserBlock>();
    public ICollection<UserBlock> BlockedByUsers { get; private set; } = new List<UserBlock>();

    public static UserProfile Create(string displayName, string passwordHash,
        PhoneNumber? phoneNumber = null, Email? email = null)
    {
        if (phoneNumber is null && email is null)
            throw new DomainRuleViolationException("A user must have a phone number or an email.");

        return new UserProfile
        {
            DisplayName = ValidateDisplayName(displayName),
            PasswordHash = ValidatePasswordHash(passwordHash),
            SecurityStamp = Guid.NewGuid(),
            PhoneNumber = phoneNumber,
            Email = email
        };
    }

    public static UserProfile Register(string displayName, string passwordHash,
        PhoneNumber? phoneNumber = null, Email? email = null)
    {
        var user = Create(displayName, passwordHash, phoneNumber, email);
        user.AddDomainEvent(new UserRegisteredEvent(user.Id));
        return user;
    }

    public void UpdateProfile(string displayName, string? avatarUrl, string? bio)
    {
        EnsureNotDeleted();
        if (bio is { Length: > MaxBioLength })
            throw new DomainRuleViolationException($"Bio cannot exceed {MaxBioLength} characters.");

        var newDisplayName = ValidateDisplayName(displayName);
        var newAvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        var newBio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        if (newDisplayName == DisplayName && newAvatarUrl == AvatarUrl && newBio == Bio) return;

        DisplayName = newDisplayName;
        AvatarUrl = newAvatarUrl;
        Bio = newBio;
        AddDomainEvent(new UserProfileUpdatedEvent(Id));
    }

    public void ChangePassword(string newPasswordHash)
    {
        EnsureNotDeleted();
        PasswordHash = ValidatePasswordHash(newPasswordHash);
        RotateSecurityStamp();
    }

    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();

    public void ChangePhoneNumber(PhoneNumber? phoneNumber)
    {
        EnsureNotDeleted();
        if (phoneNumber is null && Email is null)
            throw new DomainRuleViolationException("Cannot remove the only contact method.");
        if (phoneNumber == PhoneNumber) return;

        PhoneNumber = phoneNumber;
        AddDomainEvent(new UserProfileUpdatedEvent(Id));
    }

    public void ChangeEmail(Email? email)
    {
        EnsureNotDeleted();
        if (email is null && PhoneNumber is null)
            throw new DomainRuleViolationException("Cannot remove the only contact method.");
        if (email == Email) return;

        Email = email;
        EmailVerifiedAtUtc = null;
        AddDomainEvent(new UserProfileUpdatedEvent(Id));
    }

    public EmailVerificationToken RequestEmailVerification(string token, string tokenHash, DateTime nowUtc, TimeSpan lifetime)
    {
        EnsureNotDeleted();
        if (string.IsNullOrWhiteSpace(token))
            throw new DomainRuleViolationException("A verification token is required.");
        if (Email is null)
            throw new DomainRuleViolationException("There is no email address to verify.");
        if (IsEmailVerified)
            throw new DomainRuleViolationException("The email address is already verified.");

        var verification = EmailVerificationToken.Issue(Id, Email, tokenHash, nowUtc, lifetime);
        AddDomainEvent(new EmailVerificationRequestedEvent(Id, Email.Value, token, verification.ExpiresAtUtc));
        return verification;
    }

    public void VerifyEmail(EmailVerificationToken verification, DateTime nowUtc)
    {
        EnsureNotDeleted();
        if (verification.UserId != Id)
            throw new DomainRuleViolationException("The verification token belongs to another user.");
        if (IsEmailVerified)
            throw new DomainRuleViolationException("The email address is already verified.");
        if (Email is null || verification.Email != Email.Value)
            throw new DomainRuleViolationException("The verification token was issued for another email address.");

        verification.MarkUsed(nowUtc);
        EmailVerifiedAtUtc = nowUtc;
        AddDomainEvent(new EmailVerifiedEvent(Id));
    }

    public void SetEmailNotifications(bool enabled)
    {
        EnsureNotDeleted();
        if (EmailNotificationsEnabled == enabled) return;

        EmailNotificationsEnabled = enabled;
        AddDomainEvent(new EmailNotificationsChangedEvent(Id, enabled));
    }

    public void IssueUnsubscribeToken(string token, string tokenHash)
    {
        EnsureNotDeleted();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainRuleViolationException("An unsubscribe token and its hash are required.");

        UnsubscribeTokenHash = tokenHash;
        AddDomainEvent(new UnsubscribeTokenIssuedEvent(Id, token));
    }

    public void GoOnline()
    {
        EnsureNotDeleted();
        if (IsOnline) return;
        IsOnline = true;
        LastSeenUtc = DateTime.UtcNow;
        AddDomainEvent(new UserStatusChangedEvent(Id, true, LastSeenUtc.Value));
    }

    public void GoOffline()
    {
        if (!IsOnline) return;
        IsOnline = false;
        LastSeenUtc = DateTime.UtcNow;
        AddDomainEvent(new UserStatusChangedEvent(Id, false, LastSeenUtc.Value));
    }
    public void Delete()
    {
        GoOffline();
        IsDeleted = true;
        RotateSecurityStamp();
        AddDomainEvent(new UserDeletedEvent(Id));
    }

    private static string ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainRuleViolationException("Display name is required.");
        var trimmed = displayName.Trim();
        if (trimmed.Length > MaxDisplayNameLength)
            throw new DomainRuleViolationException($"Display name cannot exceed {MaxDisplayNameLength} characters.");
        return trimmed;
    }

    private static string ValidatePasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new DomainRuleViolationException("Password hash is required.");
        return passwordHash;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new DomainRuleViolationException("User profile is deleted.");
    }

}
