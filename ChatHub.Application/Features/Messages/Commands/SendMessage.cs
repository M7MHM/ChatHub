using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Files;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Messages.Commands;

public sealed record SendMessageCommand(
    MessageType Type,
    string? Content,
    Guid? AttachmentFileId,
    Guid? ReplyToMessageId) : IRequest<Result<MessageResponse>>
{
    public Guid ConversationId { get; init; }
}

public sealed class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.ReplyToMessageId).NotEqual(Guid.Empty);
        RuleFor(x => x.Type)
            .Must(t => t is MessageType.Text or MessageType.Image or MessageType.File or MessageType.Voice)
            .WithMessage("Type must be Text, Image, File or Voice.");
        RuleFor(x => x.Content).MaximumLength(Message.MaxContentLength);

        When(x => x.Type == MessageType.Text, () =>
        {
            RuleFor(x => x.Content).NotEmpty();
            RuleFor(x => x.AttachmentFileId).Null().WithMessage("Text messages cannot have an attachment.");
        });

        When(x => x.Type is MessageType.Image or MessageType.File or MessageType.Voice, () =>
        {
            RuleFor(x => x.AttachmentFileId)
                .NotNull()
                .NotEqual(Guid.Empty)
                .WithMessage(x => $"{x.Type} messages require an attachment.");
        });
    }
}

public sealed class SendMessageCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser, FileUrls urls)
    : IRequestHandler<SendMessageCommand, Result<MessageResponse>>
{
    public async Task<Result<MessageResponse>> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;
        var conversation = loaded.Value;

        if (conversation.Type == ConversationType.Direct &&
            conversation.Members.FirstOrDefault(m => m.UserId != me) is { } other &&
            await db.IsBlockedEitherWayAsync(me, other.UserId, cancellationToken))
            return ConversationErrors.Blocked;

        var member = conversation.FindActiveMember(me)!;

        Message? replyTo = null;
        if (request.ReplyToMessageId is { } replyToId)
        {
            replyTo = await db.FindVisibleMessageAsync(member, replyToId, cancellationToken);
            if (replyTo is null) return MessageErrors.ReplyTargetNotFound;
        }

        Attachment? attachment = null;
        if (request.AttachmentFileId is { } fileId)
        {
            var file = await db.LoadAttachableFileAsync(fileId, me, FilePurpose.MessageAttachment, cancellationToken);
            if (file.IsFailure) return file.Error;
            if (!MatchesMessageType(request.Type, file.Value.ContentType)) return FileErrors.TypeMismatch;

            file.Value.MarkAttached();
            attachment = Attachment.Create(file.Value.Id, file.Value.FileName, file.Value.ContentType, file.Value.SizeBytes);
        }

        var message = conversation.SendMessage(me, request.Type, request.Content, attachment, replyTo);
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        var response = await db.LoadForViewerAsync(message.Id, MemberVisibility.VisibleAfterUtcOf(member), urls, cancellationToken);
        return response!;
    }

    private static bool MatchesMessageType(MessageType type, string contentType) => type switch
    {
        MessageType.Image => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        MessageType.Voice => contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
        _ => true
    };
}
