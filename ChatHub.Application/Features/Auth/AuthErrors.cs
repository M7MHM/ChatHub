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

    public static readonly Error NoEmailToVerify =
        Error.Validation("Auth.NoEmailToVerify", "Your account has no email address to verify.");

    public static readonly Error EmailAlreadyVerified =
        Error.Conflict("Auth.EmailAlreadyVerified", "Your email address is already verified.");

    public static readonly Error VerificationRequestsThrottled =
        Error.TooManyRequests("Auth.VerificationThrottled",
            "Too many verification emails were requested in the last hour. Try again later.");

    public static readonly Error InvalidVerificationToken =
        Error.Validation("Auth.InvalidVerificationToken", "The verification link is invalid.");

    public static readonly Error VerificationTokenExpired =
        Error.Validation("Auth.VerificationTokenExpired",
            "The verification link has expired or was replaced by a newer one. Request a new one.");

    public static readonly Error VerificationTokenUsed =
        Error.Validation("Auth.VerificationTokenUsed", "The verification link was already used.");

    public static readonly Error InvalidResetToken =
        Error.Validation("Auth.InvalidResetToken", "The password reset link is invalid.");

    public static readonly Error ResetTokenExpired =
        Error.Validation("Auth.ResetTokenExpired",
            "The password reset link has expired or was replaced by a newer one. Request a new one.");

    public static readonly Error ResetTokenUsed =
        Error.Validation("Auth.ResetTokenUsed", "The password reset link was already used.");
}
