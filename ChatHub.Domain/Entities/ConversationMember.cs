using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class ConversationMember : BaseAuditableEntity
{
    private ConversationMember() { }

    public Guid ConversationId { get; private set; }
    public Conversation Conversation { get; private set; } = null!;
    public Guid UserId { get; private set; }
    public UserProfile User { get; private set; } = null!;
    public GroupRole Role { get; private set; } = GroupRole.Member;
    public bool IsActive { get; private set; } = true;
    public DateTime JoinedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? LeftAtUtc { get; private set; }
    public Guid? KickedByUserId { get; private set; }
    public DateTime? ClearedAtUtc { get; private set; }
    public bool IsMuted { get; private set; } = false;
    public Guid? LastReadMessageId { get; private set; }
    public DateTime? LastReadMessageAtUtc { get; private set; }
    public Guid? LastDeliveredMessageId { get; private set; }
    public DateTime? LastDeliveredMessageAtUtc { get; private set; }

    public static ConversationMember Create(Guid conversationId, Guid userId, GroupRole role = GroupRole.Member)
    {
        if (conversationId == Guid.Empty || userId == Guid.Empty)
            throw new DomainRuleViolationException("Conversation and user ids are required.");
        if (!Enum.IsDefined(role))
            throw new DomainRuleViolationException("Invalid group role.");

        return new ConversationMember { ConversationId = conversationId, UserId = userId, Role = role };
    }

    public void ChangeRole(GroupRole role)
    {
        EnsureActive();
        if (!Enum.IsDefined(role))
            throw new DomainRuleViolationException("Invalid group role.");
        Role = role;
    }

    public void Leave()
    {
        EnsureActive();
        Deactivate();
    }

    public void Kick(Guid kickedByUserId)
    {
        EnsureActive();
        if (kickedByUserId == Guid.Empty)
            throw new DomainRuleViolationException("Kicking user id is required.");
        if (kickedByUserId == UserId)
            throw new DomainRuleViolationException("A member cannot kick themselves; use Leave instead.");
        if (Role == GroupRole.Owner)
            throw new DomainRuleViolationException("The owner cannot be kicked.");

        KickedByUserId = kickedByUserId;
        Deactivate();
    }

    public void Rejoin(GroupRole role = GroupRole.Member)
    {
        if (IsActive)
            throw new DomainRuleViolationException("Member is already active.");
        IsActive = true;
        Role = role;
        JoinedAtUtc = DateTime.UtcNow;
        LeftAtUtc = null;
        KickedByUserId = null;
    }

    public void ClearChat()
    {
        EnsureActive();
        ClearedAtUtc = DateTime.UtcNow;
    }

    public void Mute()
    {
        EnsureActive();
        IsMuted = true;
    }

    public void Unmute()
    {
        EnsureActive();
        IsMuted = false;
    }

    public void MarkAsDelivered(Message message)
    {
        ValidateMessage(message);
        AdvanceDelivered(message);
    }

    public void MarkAsRead(Message message)
    {
        ValidateMessage(message);
        AdvanceDelivered(message);

        if (LastReadMessageAtUtc is { } current && message.CreatedAtUtc <= current) return;
        LastReadMessageId = message.Id;
        LastReadMessageAtUtc = message.CreatedAtUtc;
        AddDomainEvent(new MessageReadEvent(ConversationId, UserId, message.Id, DateTime.UtcNow));
    }

    private void AdvanceDelivered(Message message)
    {
        if (LastDeliveredMessageAtUtc is { } current && message.CreatedAtUtc <= current) return;
        LastDeliveredMessageId = message.Id;
        LastDeliveredMessageAtUtc = message.CreatedAtUtc;
        AddDomainEvent(new MessageDeliveredEvent(ConversationId, UserId, message.Id));
    }

    private void ValidateMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        if (message.ConversationId != ConversationId)
            throw new DomainRuleViolationException("Message does not belong to this conversation.");
    }

    private void Deactivate()
    {
        IsActive = false;
        LeftAtUtc = DateTime.UtcNow;
    }

    private void EnsureActive()
    {
        if (!IsActive) throw new DomainRuleViolationException("Member is not active in this conversation.");
    }

}
