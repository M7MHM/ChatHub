using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Users;

public static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("User.NotFound", "The user was not found.");
    public static readonly Error CannotBlockSelf = Error.Validation("User.CannotBlockSelf", "You cannot block yourself.");
}
