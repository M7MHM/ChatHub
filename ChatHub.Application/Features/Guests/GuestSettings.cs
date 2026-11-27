namespace ChatHub.Application.Features.Guests;

public sealed class GuestSettings
{
    public const string SectionName = "Guest";

    public bool Enabled { get; init; } = true;
    public int LifetimeHours { get; init; } = 24;
    public int MaxActive { get; init; } = 500;
    public int CreatePerIpPerHour { get; init; } = 5;
    public int CleanupIntervalMinutes { get; init; } = 5;

    public TimeSpan Lifetime => TimeSpan.FromHours(LifetimeHours);
    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(CleanupIntervalMinutes);
}
