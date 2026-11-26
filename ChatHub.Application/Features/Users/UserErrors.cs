using System.Globalization;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Application.Features.Users;

public static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("User.NotFound", "The user was not found.");
    public static readonly Error CannotBlockSelf = Error.Validation("User.CannotBlockSelf", "You cannot block yourself.");

    public static readonly Error InvalidUnsubscribeToken =
        Error.Validation("User.InvalidUnsubscribeToken", "The unsubscribe link is invalid or no longer active.");

    public static readonly Error UsernameInvalid = Error.Validation("User.UsernameInvalid", Username.RulesMessage);

    public static readonly Error UsernameReserved = Error.Validation("User.UsernameReserved", Username.ReservedMessage);

    public static readonly Error UsernameTaken = Error.Conflict("User.UsernameTaken", "This username is already taken.");

    public const string UsernameChangeTooSoonCode = "User.UsernameChangeTooSoon";

    public static Error UsernameChangeTooSoon(DateTime nextChangeAtUtc) =>
        Error.TooManyRequests(UsernameChangeTooSoonCode,
            $"You can change your username again after {nextChangeAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.");
}
