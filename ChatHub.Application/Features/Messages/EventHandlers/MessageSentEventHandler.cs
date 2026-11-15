using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Messages.EventHandlers;

public sealed class MessageSentEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<MessageSentEventHandler> logger) : DomainEventHandler<MessageSentEvent>(logger)
{
    protected override async Task HandleAsync(MessageSentEvent domainEvent, CancellationToken cancellationToken)
    {
        var message = await db.LoadForConversationAsync(domainEvent.MessageId, domainEvent.ConversationId, cancellationToken);
        if (message is null) return;

        await signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MessageReceived, message, cancellationToken);
    }
}
