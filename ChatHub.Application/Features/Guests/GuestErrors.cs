using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Guests;

public static class GuestErrors
{
    public const string RestrictedCode = "Guest.Restricted";

    public static readonly Error Disabled = Error.NotFound("Guest.Disabled", "Guest mode is not available.");

    public static readonly Error CapacityReached = Error.TooManyRequests("Guest.CapacityReached",
        "Too many guests are trying ChatHub right now. Try again in a little while, or create an account.");

    public static readonly Error ContactRestricted = Error.Forbidden(RestrictedCode,
        "Guest accounts can only chat with the ChatHub demo users.");

    public static readonly Error NotReachable = Error.Forbidden(RestrictedCode,
        "Guest accounts cannot be messaged or added to conversations.");

    public static readonly Error AccountRestricted = Error.Forbidden(RestrictedCode,
        "Guest accounts cannot change their username, email, phone or password.");
}
