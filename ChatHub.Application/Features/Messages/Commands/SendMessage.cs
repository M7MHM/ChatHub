using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Messages.Commands;

public sealed record SendMessageCommand(
    MessageType Type,
    string? Content,
    AttachmentInput? Attachment,
    Guid? ReplyToMessageId) : IRequest<Result<MessageResponse>>
{
    public Guid ConversationId { get; init; }
}

public sealed record AttachmentInput(string Url, string FileName, string ContentType, long SizeBytes);

public sealed class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public const string UploadsPrefix = "/uploads/";
    private const int MaxUrlLength = 2048;
    private const int MaxFileNameLength = 255;
    private const int MaxContentTypeLength = 255;

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
            RuleFor(x => x.Attachment).Null().WithMessage("Text messages cannot have an attachment.");
        });

        When(x => x.Type is MessageType.Image or MessageType.File or MessageType.Voice, () =>
        {
            RuleFor(x => x.Attachment).NotNull().WithMessage(x => $"{x.Type} messages require an attachment.");
        });

        When(x => x.Attachment is not null, () =>
        {
            RuleFor(x => x.Attachment!.Url)
                .NotEmpty()
                .MaximumLength(MaxUrlLength)
                .Must(url => url.StartsWith(UploadsPrefix, StringComparison.Ordinal) &&
                             !url.Contains("..", StringComparison.Ordinal) && !url.Contains('\\'))
                .WithMessage($"The attachment must be a file uploaded to {UploadsPrefix}.");
            RuleFor(x => x.Attachment!.FileName).NotEmpty().MaximumLength(MaxFileNameLength);
            RuleFor(x => x.Attachment!.ContentType)
                .NotEmpty()
                .MaximumLength(MaxContentTypeLength)
                .Must((command, contentType) => MatchesType(command.Type, contentType))
                .WithMessage(x => $"The content type does not match a {x.Type} message.");
            RuleFor(x => x.Attachment!.SizeBytes).GreaterThan(0);
        });
    }

    private static bool MatchesType(MessageType type, string? contentType) =>
        contentType is not null && type switch
        {
            MessageType.Image => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            MessageType.Voice => contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
            MessageType.File => true,
            _ => false
        };
}

public sealed class SendMessageCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
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

        var attachment = request.Attachment is { } a
            ? Attachment.Create(a.Url.Trim(), a.FileName.Trim(), a.ContentType.Trim(), a.SizeBytes)
            : null;

        var message = conversation.SendMessage(me, request.Type, request.Content, attachment, replyTo);
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        var response = await db.LoadForViewerAsync(message.Id, MemberVisibility.VisibleAfterUtcOf(member), cancellationToken);
        return response!;
    }
}
