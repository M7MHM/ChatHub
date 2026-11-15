using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Files.EventHandlers;

public sealed class MessageAttachmentCleanupHandler(
    IApplicationDbContext db,
    ILogger<MessageAttachmentCleanupHandler> logger) : DomainEventHandler<MessageDeletedEvent>(logger)
{
    protected override async Task HandleAsync(MessageDeletedEvent domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AttachmentFileId is not { } fileId) return;

        var file = await db.UploadedFiles.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null || file.IsDeleted) return;

        file.MarkDeleted();
        await db.SaveChangesAsync(cancellationToken);
    }
}
