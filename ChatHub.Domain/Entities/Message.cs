using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.Entities;

public class Message : BaseAuditableEntity
{
    public const int MaxContentLength = 4000;

    private Message() { }

    public Guid ConversationId { get; private set; }
    public Conversation Conversation { get; private set; } = null!;
    public Guid SenderId { get; private set; }
    public UserProfile Sender { get; private set; } = null!;
    public MessageType Type { get; private set; }
    public string? Content { get; private set; }
    public Attachment? Attachment { get; private set; }
    public Guid? ParentMessageId { get; private set; }
    public Message? ParentMessage { get; private set; }
    public bool IsEdited { get; private set; } = false;
    public DateTime? EditedAtUtc { get; private set; }

    public static Message Create(Guid conversationId, Guid senderId, MessageType type, string? content,
        Attachment? attachment = null, Guid? parentMessageId = null)
    {
        if (conversationId == Guid.Empty || senderId == Guid.Empty)
            throw new DomainRuleViolationException("Conversation and sender ids are required.");
        if (!Enum.IsDefined(type))
            throw new DomainRuleViolationException("Invalid message type.");
        if (parentMessageId == Guid.Empty)
            throw new DomainRuleViolationException("Invalid parent message id.");

        var normalized = string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        ValidatePayload(type, normalized, attachment);

        var message = new Message
        {
            ConversationId = conversationId,
            SenderId = senderId,
            Type = type,
            Content = normalized,
            Attachment = attachment,
            ParentMessageId = parentMessageId
        };
        message.CreatedById = senderId;
        message.AddDomainEvent(new MessageSentEvent(message.Id, conversationId, senderId, message.CreatedAtUtc));
        return message;
    }

    public void EditContent(Guid editorId, string newContent)
    {
        EnsureNotDeleted();
        if (editorId != SenderId)
            throw new DomainRuleViolationException("Only the sender can edit a message.");
        if (Type != MessageType.Text)
            throw new DomainRuleViolationException("Only text messages can be edited.");
        if (string.IsNullOrWhiteSpace(newContent))
            throw new DomainRuleViolationException("Message content cannot be empty.");

        var trimmed = newContent.Trim();
        ValidatePayload(Type, trimmed, null);
        if (trimmed == Content) return;

        Content = trimmed;
        IsEdited = true;
        EditedAtUtc = DateTime.UtcNow;
        AddDomainEvent(new MessageEditedEvent(Id, ConversationId));
    }

    public void Delete(Guid deletedById)
    {
        if (IsDeleted) return;
        if (deletedById != SenderId)
            throw new DomainRuleViolationException("Only the sender can delete a message.");

        IsDeleted = true;
        Content = null;
        Attachment = null;
        AddDomainEvent(new MessageDeletedEvent(Id, ConversationId));
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new DomainRuleViolationException("Message is deleted.");
    }

    private static void ValidatePayload(MessageType type, string? content, Attachment? attachment)
    {
        if (content is { Length: > MaxContentLength })
            throw new DomainRuleViolationException($"Message content cannot exceed {MaxContentLength} characters.");

        if (type is MessageType.Text or MessageType.System)
        {
            if (content is null)
                throw new DomainRuleViolationException($"{type} messages require content.");
            if (attachment is not null)
                throw new DomainRuleViolationException($"{type} messages cannot have an attachment.");
        }
        else if (attachment is null)
        {
            throw new DomainRuleViolationException($"{type} messages require an attachment.");
        }
    }
}
