namespace ChatHub.Infrastructure.RealTime;

public sealed class RealTimeSettings
{
    public const string SectionName = "RealTime";

    public int HeartbeatIntervalSeconds { get; init; } = 5;
    public int InstanceTimeoutSeconds { get; init; } = 15;

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
    public TimeSpan InstanceTimeout => TimeSpan.FromSeconds(InstanceTimeoutSeconds);
}
