using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

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
