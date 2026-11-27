using ChatHub.Application.Common.Emails;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Guests.Commands;
using ChatHub.WebAPI.Extensions;
using ChatHub.WebAPI.Pages;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatHub.WebAPI.Controllers;

public sealed class AuthController(ISender sender) : ApiController(sender)
{
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthStrict)]
    [HttpPost("auth/register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterCommand command, CancellationToken cancellationToken) =>
        ToCreated(await Sender.Send(command, cancellationToken), auth => $"/api/users/{auth.User.Id}");

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.GuestCreate)]
    [HttpPost("auth/guest")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AuthResponse>> ContinueAsGuest(CancellationToken cancellationToken) =>
        ToCreated(await Sender.Send(new CreateGuestCommand(), cancellationToken), auth => $"/api/users/{auth.User.Id}");

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthStrict)]
    [HttpPost("auth/login")]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginCommand command, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthSession)]
    [HttpPost("auth/refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(
        [FromBody] RefreshCommand command, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthSession)]
    [HttpPost("auth/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [EnableRateLimiting(RateLimitPolicies.AuthSession)]
    [HttpPost("auth/change-password")]
    public async Task<ActionResult<AuthResponse>> ChangePassword(
        [FromBody] ChangePasswordCommand command, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PasswordReset)]
    [HttpPost("auth/forgot-password")]
    [ProducesResponseType<ForgotPasswordResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword(
        [FromBody] ForgotPasswordCommand command, CancellationToken cancellationToken) =>
        ToAccepted(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthStrict)]
    [HttpPost("auth/reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [EnableRateLimiting(RateLimitPolicies.EmailVerification)]
    [HttpPost("auth/email-verification")]
    [ProducesResponseType<EmailVerificationResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<EmailVerificationResponse>> RequestEmailVerification(CancellationToken cancellationToken) =>
        ToAccepted(await Sender.Send(new RequestEmailVerificationCommand(), cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.EmailLink)]
    [HttpPost(EmailLinks.VerifyEmailRoute)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.EmailLink)]
    [HttpGet(EmailLinks.VerifyEmailRoute)]
    [Produces("text/html")]
    public async Task<ContentResult> VerifyEmailPage([FromQuery] string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > VerifyEmailCommandValidator.MaxTokenLength)
            return VerificationPage(AuthErrors.InvalidVerificationToken);

        var result = await Sender.Send(new VerifyEmailCommand(token), cancellationToken);
        return VerificationPage(result.IsSuccess ? null : result.Error);
    }

    private ContentResult VerificationPage(Error? error)
    {
        var (status, title, message) = error?.Code switch
        {
            null => (StatusCodes.Status200OK, "Email verified",
                "Your email address is verified. You will now get email notifications from ChatHub. You can close this page."),
            var code when code == AuthErrors.EmailAlreadyVerified.Code => (StatusCodes.Status200OK, "Already verified",
                "Your email address was already verified. You can close this page."),
            var code when code == AuthErrors.VerificationTokenExpired.Code => (StatusCodes.Status410Gone, "Link expired",
                "This verification link has expired or was replaced by a newer one. Sign in to ChatHub and request a new email."),
            var code when code == AuthErrors.VerificationTokenUsed.Code => (StatusCodes.Status410Gone, "Link already used",
                "This verification link was already used. Sign in to ChatHub to check your email settings."),
            _ => (StatusCodes.Status400BadRequest, "Invalid link",
                "This verification link is not valid. Check that you copied the whole link, or request a new email in ChatHub.")
        };
        return HtmlPage.Create(Response, status, title, message);
    }
}
