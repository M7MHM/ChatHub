using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
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
    public DateTime? LastReadAtUtc { get; private set; }

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
        Touch();
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
        Touch();
    }

    public void ClearChat()
    {
        EnsureActive();
        ClearedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void Mute()
    {
        EnsureActive();
        IsMuted = true;
        Touch();
    }

    public void Unmute()
    {
        EnsureActive();
        IsMuted = false;
        Touch();
    }

    public void MarkAsRead(Guid messageId)
    {
        EnsureActive();
        if (messageId == Guid.Empty)
            throw new DomainRuleViolationException("Message id is required.");
        LastReadMessageId = messageId;
        LastReadAtUtc = DateTime.UtcNow;
        Touch();
    }

    private void Deactivate()
    {
        IsActive = false;
        LeftAtUtc = DateTime.UtcNow;
        Touch();
    }

    private void EnsureActive()
    {
        if (!IsActive) throw new DomainRuleViolationException("Member is not active in this conversation.");
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
