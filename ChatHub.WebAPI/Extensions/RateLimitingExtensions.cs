using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatHub.WebAPI.Extensions;

public static class RateLimitPolicies
{
    public const string AuthStrict = "auth-strict";
    public const string AuthSession = "auth-session";
    public const string EmailVerification = "email-verification";
    public const string EmailLink = "email-link";
    public const string PasswordReset = "password-reset";
    public const string UsernameCheck = "username-check";
    public const string GuestCreate = "guest-create";
}

public static class RateLimitingExtensions
{
    public const string SectionName = "RateLimiting";
    public const int DefaultAuthStrictPermitLimit = 10;
    public const int DefaultAuthSessionPermitLimit = 60;
    public const int DefaultEmailVerificationPermitLimit = 5;
    public const int DefaultEmailLinkPermitLimit = 30;
    public const int DefaultPasswordResetPermitLimit = 5;
    public const int DefaultUsernameCheckPermitLimit = 30;
    public const int DefaultGuestCreatePermitLimit = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var authStrict = PermitLimit(section, "AuthStrictPermitLimit", DefaultAuthStrictPermitLimit);
        var authSession = PermitLimit(section, "AuthSessionPermitLimit", DefaultAuthSessionPermitLimit);
        var emailVerification = PermitLimit(section, "EmailVerificationPermitLimit", DefaultEmailVerificationPermitLimit);
        var emailLink = PermitLimit(section, "EmailLinkPermitLimit", DefaultEmailLinkPermitLimit);
        var passwordReset = PermitLimit(section, "PasswordResetPermitLimit", DefaultPasswordResetPermitLimit);
        var usernameCheck = PermitLimit(section, "UsernameCheckPermitLimit", DefaultUsernameCheckPermitLimit);
        var guestCreate = PermitLimit(configuration.GetSection("Guest"), "CreatePerIpPerHour", DefaultGuestCreatePermitLimit);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddFixedWindowPerIp(RateLimitPolicies.AuthStrict, authStrict);
            options.AddFixedWindowPerIp(RateLimitPolicies.AuthSession, authSession);
            options.AddFixedWindowPerIp(RateLimitPolicies.EmailVerification, emailVerification);
            options.AddFixedWindowPerIp(RateLimitPolicies.EmailLink, emailLink);
            options.AddFixedWindowPerIp(RateLimitPolicies.PasswordReset, passwordReset);
            options.AddFixedWindowPerIp(RateLimitPolicies.UsernameCheck, usernameCheck);
            options.AddFixedWindowPerIp(RateLimitPolicies.GuestCreate, guestCreate, TimeSpan.FromHours(1));

            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .TryWriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context.HttpContext,
                        ProblemDetails =
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = "Too many requests.",
                            Detail = "Too many attempts from this address. Try again later."
                        }
                    });
            };
        });
    }

    private static int PermitLimit(IConfigurationSection section, string key, int defaultValue)
    {
        var value = section.GetValue(key, defaultValue);
        return value > 0 ? value : throw new InvalidOperationException($"{SectionName}:{key} must be greater than zero.");
    }

    private static void AddFixedWindowPerIp(this RateLimiterOptions options, string policyName, int permitLimit,
        TimeSpan? window = null) =>
        options.AddPolicy(policyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window ?? Window,
                QueueLimit = 0
            }));
}
