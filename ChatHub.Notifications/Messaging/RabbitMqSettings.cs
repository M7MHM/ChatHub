namespace ChatHub.Notifications.Messaging;

public sealed class RabbitMqSettings
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string User { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string VirtualHost { get; init; } = "/";
    public ushort PrefetchCount { get; init; } = 16;
    public int MaxDeliveryAttempts { get; init; } = 5;
    public int RetryDelayMilliseconds { get; init; } = 5000;

    public TimeSpan RetryDelayFor(int failedAttempts) =>
        TimeSpan.FromMilliseconds(RetryDelayMilliseconds * Math.Pow(2, failedAttempts - 1));
}
