namespace ChatHub.Notifications.Channels.Email;

public sealed class SmtpSettings
{
    public const string SectionName = "Smtp";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1025;
    public string? User { get; init; }
    public string? Password { get; init; }
    public SmtpSecurity Security { get; init; } = SmtpSecurity.None;
    public string FromAddress { get; init; } = "notifications@chathub.local";
    public string FromName { get; init; } = "ChatHub";
    public int TimeoutSeconds { get; init; } = 30;
    public int IdleTimeoutSeconds { get; init; } = 30;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(Math.Max(0, IdleTimeoutSeconds));
}

public enum SmtpSecurity
{
    None,
    StartTls,
    SslOnConnect,
    Auto
}
