using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatHub.WebAPI.Extensions;

public static class RateLimitPolicies
{
    public const string AuthStrict = "auth-strict";
    public const string AuthSession = "auth-session";
}

public static class RateLimitingExtensions
{
    public const string SectionName = "RateLimiting";
    public const int DefaultAuthStrictPermitLimit = 10;
    public const int DefaultAuthSessionPermitLimit = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var authStrict = PermitLimit(section, "AuthStrictPermitLimit", DefaultAuthStrictPermitLimit);
        var authSession = PermitLimit(section, "AuthSessionPermitLimit", DefaultAuthSessionPermitLimit);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddFixedWindowPerIp(RateLimitPolicies.AuthStrict, authStrict);
            options.AddFixedWindowPerIp(RateLimitPolicies.AuthSession, authSession);

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

    private static void AddFixedWindowPerIp(this RateLimiterOptions options, string policyName, int permitLimit) =>
        options.AddPolicy(policyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = Window,
                QueueLimit = 0
            }));
}
