using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.Entities;

public class Conversation : BaseAuditableEntity
{
    public const int MaxTitleLength = 100;

    private Conversation() { }

    public ConversationType Type { get; private set; }
    public string? Title { get; private set; }
    public string? ImageUrl { get; private set; }
    public Guid? LastMessageId { get; private set; }
    public DateTime? LastMessageAtUtc { get; private set; }

    public ICollection<ConversationMember> Members { get; private set; } = new List<ConversationMember>();
    public ICollection<Message> Messages { get; private set; } = new List<Message>();

    public static Conversation CreateDirect(Guid firstUserId, Guid secondUserId)
    {
        if (firstUserId == Guid.Empty || secondUserId == Guid.Empty)
            throw new DomainRuleViolationException("Both user ids are required.");
        if (firstUserId == secondUserId)
            throw new DomainRuleViolationException("A direct conversation needs two different users.");

        var conversation = new Conversation { Type = ConversationType.Direct };
        conversation.Members.Add(ConversationMember.Create(conversation.Id, firstUserId));
        conversation.Members.Add(ConversationMember.Create(conversation.Id, secondUserId));
        return conversation;
    }

    public static Conversation CreateGroup(Guid ownerId, string title, string? imageUrl = null)
    {
        if (ownerId == Guid.Empty)
            throw new DomainRuleViolationException("Owner id is required.");

        var conversation = new Conversation
        {
            Type = ConversationType.Group,
            Title = ValidateTitle(title),
            ImageUrl = NormalizeUrl(imageUrl)
        };
        conversation.Members.Add(ConversationMember.Create(conversation.Id, ownerId, GroupRole.Owner));
        return conversation;
    }

    public void UpdateGroupInfo(Guid actorId, string title, string? imageUrl)
    {
        EnsureGroup();
        RequireRole(actorId, GroupRole.Admin);
        Title = ValidateTitle(title);
        ImageUrl = NormalizeUrl(imageUrl);
        Touch();
    }

    public ConversationMember AddMember(Guid actorId, Guid userId)
    {
        EnsureGroup();
        RequireRole(actorId, GroupRole.Admin);

        var existing = FindMember(userId);
        if (existing is { IsActive: true })
            throw new DomainRuleViolationException("User is already a member of this conversation.");

        if (existing is not null)
        {
            existing.Rejoin();
            Touch();
            return existing;
        }

        var member = ConversationMember.Create(Id, userId);
        Members.Add(member);
        Touch();
        return member;
    }

    public void KickMember(Guid actorId, Guid targetUserId)
    {
        EnsureGroup();
        var actor = RequireRole(actorId, GroupRole.Admin);
        var target = FindActiveMember(targetUserId)
            ?? throw new DomainRuleViolationException("Target user is not an active member.");

        if (target.Role >= actor.Role)
            throw new DomainRuleViolationException("You can only kick members with a lower role than yours.");

        target.Kick(actorId);
        AddDomainEvent(new MemberKickedEvent(Id, targetUserId, actorId));
        Touch();
    }

    public void LeaveMember(Guid userId)
    {
        var member = FindActiveMember(userId)
            ?? throw new DomainRuleViolationException("User is not an active member.");

        if (Type == ConversationType.Group && member.Role == GroupRole.Owner &&
            Members.Any(m => m.IsActive && m.UserId != userId))
            throw new DomainRuleViolationException("The owner must transfer ownership before leaving.");

        member.Leave();
        Touch();
    }

    public void ChangeMemberRole(Guid actorId, Guid targetUserId, GroupRole newRole)
    {
        EnsureGroup();
        if (newRole == GroupRole.Owner)
            throw new DomainRuleViolationException("Use TransferOwnership to assign the owner role.");

        RequireRole(actorId, GroupRole.Owner);
        var target = FindActiveMember(targetUserId)
            ?? throw new DomainRuleViolationException("Target user is not an active member.");
        if (target.Role == GroupRole.Owner)
            throw new DomainRuleViolationException("The role of the owner cannot be changed.");

        target.ChangeRole(newRole);
        Touch();
    }

    public void TransferOwnership(Guid currentOwnerId, Guid newOwnerId)
    {
        EnsureGroup();
        var owner = RequireRole(currentOwnerId, GroupRole.Owner);
        var newOwner = FindActiveMember(newOwnerId)
            ?? throw new DomainRuleViolationException("New owner must be an active member.");
        if (newOwner == owner)
            throw new DomainRuleViolationException("User is already the owner.");

        owner.ChangeRole(GroupRole.Admin);
        newOwner.ChangeRole(GroupRole.Owner);
        Touch();
    }

    public Message SendMessage(Guid senderId, MessageType type, string? content,
        Attachment? attachment = null, Guid? parentMessageId = null)
    {
        if (type != MessageType.System && FindActiveMember(senderId) is null)
            throw new DomainRuleViolationException("Only active members can send messages.");

        var message = Message.Create(Id, senderId, type, content, attachment, parentMessageId);
        Messages.Add(message);
        LastMessageId = message.Id;
        LastMessageAtUtc = message.CreatedAtUtc;
        Touch();
        return message;
    }

    public ConversationMember? FindMember(Guid userId) => Members.FirstOrDefault(m => m.UserId == userId);

    public ConversationMember? FindActiveMember(Guid userId) =>
        Members.FirstOrDefault(m => m.UserId == userId && m.IsActive);

    private ConversationMember RequireRole(Guid userId, GroupRole minimum)
    {
        var member = FindActiveMember(userId)
            ?? throw new DomainRuleViolationException("User is not an active member of this conversation.");
        if (member.Role < minimum)
            throw new DomainRuleViolationException($"This action requires the {minimum} role or higher.");
        return member;
    }

    private void EnsureGroup()
    {
        if (Type != ConversationType.Group)
            throw new DomainRuleViolationException("This action is only valid for group conversations.");
    }

    private static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainRuleViolationException("Group title is required.");
        var trimmed = title.Trim();
        if (trimmed.Length > MaxTitleLength)
            throw new DomainRuleViolationException($"Group title cannot exceed {MaxTitleLength} characters.");
        return trimmed;
    }

    private static string? NormalizeUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
