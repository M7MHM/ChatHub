using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Auth;

public static class AuthErrors
{
    public static readonly Error ContactInUse =
        Error.Conflict("Auth.ContactInUse", "The email or phone number is already in use.");

    public static readonly Error InvalidCredentials =
        Error.Validation("Auth.InvalidCredentials", "The identifier or password is incorrect.");

    public static readonly Error InvalidRefreshToken =
        Error.Validation("Auth.InvalidRefreshToken", "The refresh token is invalid or expired.");

    public static readonly Error TokenReused =
        Error.Validation("Auth.TokenReused", "The refresh token was already used. All sessions have been signed out.");

    public static readonly Error InvalidCurrentPassword =
        Error.Validation("Auth.InvalidCurrentPassword", "The current password is incorrect.");
}
