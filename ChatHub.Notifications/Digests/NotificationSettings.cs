namespace ChatHub.Notifications.Digests;

public sealed class NotificationSettings
{
    public const string SectionName = "Notifications";

    public int DigestDelaySeconds { get; init; } = 120;
    public int CooldownMinutes { get; init; } = 15;
    public bool IncludePreview { get; init; } = true;
    public string AppBaseUrl { get; init; } = "http://localhost:3000";
    public int MaxAttempts { get; init; } = 5;
    public int RetryBaseDelaySeconds { get; init; } = 30;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public int LeaseSeconds { get; init; } = 120;
    public int BatchSize { get; init; } = 20;
    public int InboxRetentionDays { get; init; } = 7;
    public int MaxMessageAgeHours { get; init; } = 24;
    public int VerificationRecheckSeconds { get; init; } = 60;

    public TimeSpan DigestDelay => TimeSpan.FromSeconds(DigestDelaySeconds);
    public TimeSpan Cooldown => TimeSpan.FromMinutes(CooldownMinutes);
    public TimeSpan RetryBaseDelay => TimeSpan.FromSeconds(RetryBaseDelaySeconds);
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan InboxRetention => TimeSpan.FromDays(InboxRetentionDays);
    public TimeSpan MaxMessageAge => TimeSpan.FromHours(MaxMessageAgeHours);
    public TimeSpan VerificationRecheck => TimeSpan.FromSeconds(VerificationRecheckSeconds);

    public string ConversationLink(Guid conversationId) => $"{AppBaseUrl.TrimEnd('/')}/conversations/{conversationId}";
}
