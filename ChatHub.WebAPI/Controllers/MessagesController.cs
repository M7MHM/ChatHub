using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Messages.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

public sealed class MessagesController(ISender sender) : ApiController(sender)
{
    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<CursorPage<MessageResponse>>> GetMessages(
        Guid conversationId, [FromQuery] string? cursor, CancellationToken cancellationToken,
        [FromQuery] int pageSize = GetMessagesQuery.DefaultPageSize) =>
        ToOk(await Sender.Send(new GetMessagesQuery(cursor, pageSize) { ConversationId = conversationId }, cancellationToken));

    [HttpPost("conversations/{conversationId:guid}/messages")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MessageResponse>> Send(
        Guid conversationId, [FromBody] SendMessageCommand command, CancellationToken cancellationToken) =>
        ToCreated(await Sender.Send(command with { ConversationId = conversationId }, cancellationToken),
            message => $"/api/messages/{message.Id}");

    [HttpPost("conversations/{conversationId:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAsRead(
        Guid conversationId, [FromBody] MarkAsReadCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = conversationId }, cancellationToken));

    [HttpPost("conversations/{conversationId:guid}/delivered")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAsDelivered(
        Guid conversationId, [FromBody] MarkAsDeliveredCommand command, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(command with { ConversationId = conversationId }, cancellationToken));

    [HttpPut("messages/{id:guid}")]
    public async Task<ActionResult<MessageResponse>> Edit(
        Guid id, [FromBody] EditMessageCommand command, CancellationToken cancellationToken) =>
        ToOk(await Sender.Send(command with { MessageId = id }, cancellationToken));

    [HttpDelete("messages/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        ToNoContent(await Sender.Send(new DeleteMessageCommand(id), cancellationToken));
}
