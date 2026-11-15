using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.WebAPI.Extensions;
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
}
