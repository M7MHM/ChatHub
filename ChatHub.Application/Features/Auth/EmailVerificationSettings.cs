namespace ChatHub.Application.Features.Auth;

public sealed class EmailVerificationSettings
{
    public const string SectionName = "EmailVerification";

    public int TokenLifetimeHours { get; init; } = 24;
    public int MaxRequestsPerHour { get; init; } = 3;

    public TimeSpan TokenLifetime => TimeSpan.FromHours(TokenLifetimeHours);
    public static TimeSpan ThrottleWindow => TimeSpan.FromHours(1);
}
