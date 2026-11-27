using ChatHub.Application.Common.Caching;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Infrastructure.Identity;

internal static class SecurityStampValidator
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var userId = context.Principal.GetUserId();
        var stamp = context.Principal.GetSecurityStamp();
        if (userId is null || stamp is null)
        {
            context.Fail("The access token has no user id or security stamp.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var user = await services.GetRequiredService<HybridCache>().GetOrCreateAsync(
            CacheKeys.SecurityStamp(userId.Value),
            (scopeFactory: services.GetRequiredService<IServiceScopeFactory>(), userId: userId.Value),
            static async (state, token) =>
            {
                await using var scope = state.scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await db.UserProfiles
                    .AsNoTracking()
                    .Where(u => u.Id == state.userId)
                    .Select(u => new UserStamp(u.IsDeleted, u.SecurityStamp, u.GuestExpiresAtUtc))
                    .FirstOrDefaultAsync(token);
            },
            CacheKeys.SecurityStampEntryOptions,
            cancellationToken: context.HttpContext.RequestAborted);

        var nowUtc = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        if (user is null || user.IsDeleted || user.SecurityStamp != stamp.Value || user.GuestExpiresAtUtc <= nowUtc)
            context.Fail("The access token is no longer valid.");
    }

    internal sealed record UserStamp(bool IsDeleted, Guid SecurityStamp, DateTime? GuestExpiresAtUtc = null);
}
