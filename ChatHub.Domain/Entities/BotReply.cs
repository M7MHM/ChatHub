using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class BotReply : BaseEntity
{
    public const int MaxTriggerTextLength = 200;

    private BotReply() { }

    public Guid ConversationId { get; private set; }
    public Guid RequestedById { get; private set; }
    public string? TriggerText { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime DueAtUtc { get; private set; }
    public DateTime? LockedUntilUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public Guid? ReplyMessageId { get; private set; }

    public bool IsCompleted => CompletedAtUtc is not null;

    public static BotReply Schedule(Guid triggerMessageId, Guid conversationId, Guid requestedById, string? triggerText,
        DateTime nowUtc, TimeSpan delay)
    {
        if (triggerMessageId == Guid.Empty || conversationId == Guid.Empty || requestedById == Guid.Empty)
            throw new DomainRuleViolationException("Trigger message, conversation and requester ids are required.");
        if (delay < TimeSpan.Zero)
            throw new DomainRuleViolationException("A bot reply cannot be due in the past.");

        var text = string.IsNullOrWhiteSpace(triggerText) ? null : triggerText.Trim();
        return new BotReply
        {
            Id = triggerMessageId,
            ConversationId = conversationId,
            RequestedById = requestedById,
            TriggerText = text is { Length: > MaxTriggerTextLength } ? text[..MaxTriggerTextLength] : text,
            RequestedAtUtc = nowUtc,
            DueAtUtc = nowUtc + delay
        };
    }

    public void Complete(Guid replyMessageId, DateTime nowUtc)
    {
        if (replyMessageId == Guid.Empty)
            throw new DomainRuleViolationException("The reply message id is required.");
        EnsureOpen();
        ReplyMessageId = replyMessageId;
        CompletedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    public void Abandon(DateTime nowUtc)
    {
        EnsureOpen();
        CompletedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    private void EnsureOpen()
    {
        if (IsCompleted)
            throw new DomainRuleViolationException("The bot reply was already completed.");
    }
}
