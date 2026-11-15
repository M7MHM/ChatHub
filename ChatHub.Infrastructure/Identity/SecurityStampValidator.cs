using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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

        var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var user = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.IsDeleted, u.SecurityStamp })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (user is null || user.IsDeleted || user.SecurityStamp != stamp.Value)
            context.Fail("The access token is no longer valid.");
    }
}
