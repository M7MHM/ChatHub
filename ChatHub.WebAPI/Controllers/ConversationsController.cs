using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Messages.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

public sealed class ConversationsController(ISender sender) : ApiController(sender)
{
    [HttpGet("conversations")]
    public async Task<ActionResult<CursorPage<ConversationListItem>>> GetMine(
        [FromQuery] string? cursor, CancellationToken cancellationToken,
        [FromQuery] int pageSize = GetMyConversationsQuery.DefaultPageSize) =>
        ToOk(await Sender.Send(new GetMyConversationsQuery(cursor, pageSize), cancellationToken));

    [HttpGet("conversations/{id:guid}")]
    public async Task<ActionResult<ConversationDetailsResponse>> GetDetails(Guid id, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new GetConversationDetailsQuery(id), cancellationToken));

    [HttpPost("conversations/direct")]
    [ProducesResponseType<CreateDirectConversationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<CreateDirectConversationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CreateDirectConversationResponse>> CreateDirect(
        [FromBody] CreateDirectConversationCommand command, CancellationToken cancellationToken) =>
        ToCreatedOrOk(await Sender.Send(command, cancellationToken),
            direct => direct.Created, direct => $"/api/conversations/{direct.ConversationId}");

    [HttpPost("conversations/groups")]
    [ProducesResponseType<CreateGroupResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateGroupResponse>> CreateGroup(
        [FromBody] CreateGroupCommand command, CancellationToken cancellationToken) =>
        ToCreated(await Sender.Send(command, cancellationToken), group => $"/api/conversations/{group.ConversationId}");

    [HttpPut("conversations/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateGroupInfo(
        Guid id, [FromBody] UpdateGroupInfoCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = id }, cancellationToken));

    [HttpPost("conversations/{id:guid}/members")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> AddMember(
        Guid id, [FromBody] AddMemberCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = id }, cancellationToken));

    [HttpDelete("conversations/{id:guid}/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> KickMember(Guid id, Guid userId, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new KickMemberCommand(id, userId), cancellationToken));

    [HttpPut("conversations/{id:guid}/members/{userId:guid}/role")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeMemberRole(
        Guid id, Guid userId, [FromBody] ChangeMemberRoleCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = id, UserId = userId }, cancellationToken));

    [HttpPost("conversations/{id:guid}/transfer-ownership")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> TransferOwnership(
        Guid id, [FromBody] TransferOwnershipCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = id }, cancellationToken));

    [HttpPost("conversations/{id:guid}/leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Leave(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new LeaveConversationCommand(id), cancellationToken));

    [HttpPost("conversations/{id:guid}/mute")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Mute(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new MuteConversationCommand(id), cancellationToken));

    [HttpDelete("conversations/{id:guid}/mute")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unmute(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new UnmuteConversationCommand(id), cancellationToken));

    [HttpPost("conversations/{id:guid}/clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Clear(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new ClearChatCommand(id), cancellationToken));

    [HttpGet("conversations/{id:guid}/read-state")]
    public async Task<ActionResult<IReadOnlyList<MemberReadState>>> GetReadState(
        Guid id, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(new GetReadStateQuery(id), cancellationToken));
}
