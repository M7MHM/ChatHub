using System.Security.Claims;

namespace ChatHub.Infrastructure.Identity;

public static class ClaimsPrincipalExtensions
{
    public const string SecurityStampClaim = "sstamp";

    public static Guid? GetUserId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static Guid? GetSecurityStamp(this ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(SecurityStampClaim), out var stamp) ? stamp : null;
}
