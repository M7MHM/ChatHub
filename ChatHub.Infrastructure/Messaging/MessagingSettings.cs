namespace ChatHub.Infrastructure.Messaging;

public sealed class MessagingSettings
{
    public const string SectionName = "Messaging";
    public const string EnabledSetting = SectionName + ":Enabled";

    public bool Enabled { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string User { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string VirtualHost { get; init; } = "/";
    public int BatchSize { get; init; } = 100;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public int RetentionDays { get; init; } = 3;
    public int CleanupIntervalMinutes { get; init; } = 60;
    public int PublishTimeoutSeconds { get; init; } = 15;
    public int MaxRetryDelaySeconds { get; init; } = 300;
    public int BacklogAlertMinutes { get; init; } = 5;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(CleanupIntervalMinutes);
    public TimeSpan PublishTimeout => TimeSpan.FromSeconds(PublishTimeoutSeconds);
    public TimeSpan Lease => 2 * PublishTimeout + TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay => TimeSpan.FromSeconds(MaxRetryDelaySeconds);
    public TimeSpan BacklogAlert => TimeSpan.FromMinutes(BacklogAlertMinutes);
}
