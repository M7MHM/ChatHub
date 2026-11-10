using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class MessageReceipt : BaseEntity
{
    private MessageReceipt() { }

    public Guid MessageId { get; private set; }
    public Message Message { get; private set; } = null!;
    public Guid UserId { get; private set; }
    public UserProfile User { get; private set; } = null!;
    public ReceiptStatus Status { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;

    public static MessageReceipt Create(Guid messageId, Guid userId, ReceiptStatus status = ReceiptStatus.Sent)
    {
        if (messageId == Guid.Empty || userId == Guid.Empty)
            throw new DomainRuleViolationException("Message and user ids are required.");
        if (!Enum.IsDefined(status))
            throw new DomainRuleViolationException("Invalid receipt status.");

        return new MessageReceipt { MessageId = messageId, UserId = userId, Status = status };
    }
    public void Advance(ReceiptStatus status)
    {
        if (!Enum.IsDefined(status))
            throw new DomainRuleViolationException("Invalid receipt status.");
        if (status <= Status) return;

        Status = status;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkAsDelivered() => Advance(ReceiptStatus.Delivered);

    public void MarkAsRead() => Advance(ReceiptStatus.Read);
}
