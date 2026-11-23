using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;

namespace ChatHub.Notifications.Persistence;

public sealed class Recipient
{
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 100;
    public const int MaxLinkLength = 2048;

    public Guid UserId { get; set; }
    public string? Email { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public bool EmailVerified { get; set; }
    public bool EmailNotificationsEnabled { get; set; } = true;
    public string? UnsubscribeLink { get; set; }
    public DateTime? UnsubscribeLinkIssuedAtUtc { get; set; }
    public DateTime LastEventAtUtc { get; set; }

    public bool CanReceiveEmailDigests => HasLiveEmail && EmailVerified && EmailNotificationsEnabled;

    public bool AwaitsEmailVerification => HasLiveEmail && !EmailVerified && EmailNotificationsEnabled;

    private bool HasLiveEmail => !IsDeleted && !string.IsNullOrWhiteSpace(Email);
}

public sealed class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}

public sealed class ConversationReadMarker
{
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTime ReadUpToUtc { get; set; }
}

public sealed class PendingDigest
{
    public const int MaxTitleLength = 100;
    public const int MaxSenderNameLength = 100;
    public const int MaxPreviewLength = 200;
    public const int MaxErrorLength = 500;

    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public ConversationType ConversationType { get; set; }
    public string? ConversationTitle { get; set; }
    public DateTime FirstUnreadAtUtc { get; set; }
    public DateTime LastMessageAtUtc { get; set; }
    public int MessageCount { get; set; }
    public string LastSenderName { get; set; } = string.Empty;
    public string? LastPreview { get; set; }
    public DateTime DueAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? FailedAtUtc { get; set; }
}

public sealed class DigestMessage
{
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid ChatMessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public sealed class DigestCooldown
{
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTime LastSentAtUtc { get; set; }
}

public sealed class SentNotification
{
    public const int MaxChannelLength = 20;
    public const int MaxKindLength = 20;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public NotificationKind Kind { get; set; }
    public int MessageCount { get; set; }
    public DateTime SentAtUtc { get; set; }
}

public sealed class MessageSnapshot
{
    public Guid ChatMessageId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? SenderName { get; set; }
    public string? Preview { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}

public sealed class RecipientPresence
{
    public Guid UserId { get; set; }
    public bool IsOnline { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

public enum OutgoingEmailTemplate
{
    EmailVerification
}

public sealed class OutgoingEmail
{
    public const int MaxTemplateLength = 40;
    public const int MaxErrorLength = 500;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public OutgoingEmailTemplate Template { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? FailedAtUtc { get; set; }
}
