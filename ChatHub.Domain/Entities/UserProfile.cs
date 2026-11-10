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
    public string? AvatarUrl { get; private set; }
    public string? Bio { get; private set; }
    public bool IsOnline { get; private set; } = false;
    public DateTime? LastSeenUtc { get; private set; }

    public ICollection<ConversationMember> Memberships { get; private set; } = new List<ConversationMember>();
    public ICollection<Message> SentMessages { get; private set; } = new List<Message>();
    public ICollection<UserBlock> BlockedUsers { get; private set; } = new List<UserBlock>();
    public ICollection<UserBlock> BlockedByUsers { get; private set; } = new List<UserBlock>();

    public static UserProfile Create(string displayName, PhoneNumber? phoneNumber = null, Email? email = null)
    {
        if (phoneNumber is null && email is null)
            throw new DomainRuleViolationException("A user must have a phone number or an email.");

        return new UserProfile
        {
            DisplayName = ValidateDisplayName(displayName),
            PhoneNumber = phoneNumber,
            Email = email
        };
    }
    public void UpdateProfile(string displayName, string? avatarUrl, string? bio)
    {
        EnsureNotDeleted();
        if (bio is { Length: > MaxBioLength })
            throw new DomainRuleViolationException($"Bio cannot exceed {MaxBioLength} characters.");

        DisplayName = ValidateDisplayName(displayName);
        AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim();
        Touch();
    }
    public void ChangePhoneNumber(PhoneNumber? phoneNumber)
    {
        EnsureNotDeleted();
        if (phoneNumber is null && Email is null)
            throw new DomainRuleViolationException("Cannot remove the only contact method.");
        PhoneNumber = phoneNumber;
        Touch();
    }

    public void ChangeEmail(Email? email)
    {
        EnsureNotDeleted();
        if (email is null && PhoneNumber is null)
            throw new DomainRuleViolationException("Cannot remove the only contact method.");
        Email = email;
        Touch();
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
        Touch();
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

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new DomainRuleViolationException("User profile is deleted.");
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
