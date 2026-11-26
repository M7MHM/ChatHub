using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.WebAPI.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatHub.WebAPI.Controllers;

public sealed class UsersController(ISender sender) : ApiController(sender)
{
    [HttpGet("users/me")]
    public async Task<ActionResult<MeResponse>> GetMe(CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new GetMeQuery(), cancellationToken));

    [HttpPut("users/me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateMe(
        [FromBody] UpdateMyProfileCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [HttpPut("users/me/username")]
    public async Task<ActionResult<UsernameResponse>> ChangeUsername(
        [FromBody] ChangeUsernameCommand command, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(command, cancellationToken));

    [HttpPut("users/me/privacy")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdatePrivacy(
        [FromBody] UpdatePrivacySettingsCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.UsernameCheck)]
    [HttpGet("users/username-availability")]
    public async Task<ActionResult<UsernameAvailabilityResponse>> CheckUsernameAvailability(
        [FromQuery] string? username, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new CheckUsernameAvailabilityQuery(username ?? string.Empty), cancellationToken));

    [HttpPut("users/me/email-preferences")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateEmailPreferences(
        [FromBody] UpdateEmailPreferencesCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command, cancellationToken));

    [HttpDelete("users/me")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteMe(CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new DeleteMyAccountCommand(), cancellationToken));

    [HttpGet("users/me/blocked")]
    public async Task<ActionResult<IReadOnlyList<BlockedUserResponse>>> GetMyBlockedUsers(
        CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new GetMyBlockedUsersQuery(), cancellationToken));

    [HttpGet("users/search")]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> Search(
        [FromQuery] string? query, CancellationToken cancellationToken,
        [FromQuery] int pageSize = SearchUsersQuery.MaxPageSize) =>
        ToOk(await Sender.Send(new SearchUsersQuery(query ?? string.Empty, pageSize), cancellationToken));

    [HttpGet("users/{id:guid}")]
    public async Task<ActionResult<UserProfileResponse>> GetProfile(Guid id, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new GetUserProfileQuery(id), cancellationToken));

    [HttpPost("users/{id:guid}/block")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Block(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new BlockUserCommand(id), cancellationToken));

    [HttpDelete("users/{id:guid}/block")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new UnblockUserCommand(id), cancellationToken));
}
