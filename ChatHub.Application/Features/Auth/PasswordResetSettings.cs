namespace ChatHub.Application.Features.Auth;

public sealed class PasswordResetSettings
{
    public const string SectionName = "PasswordReset";

    public int TokenLifetimeMinutes { get; init; } = 30;
    public int MaxRequestsPerHour { get; init; } = 3;
    public int MinimumResponseMilliseconds { get; init; } = 300;

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(TokenLifetimeMinutes);
    public TimeSpan MinimumResponseTime => TimeSpan.FromMilliseconds(MinimumResponseMilliseconds);
    public static TimeSpan ThrottleWindow => TimeSpan.FromHours(1);
}
