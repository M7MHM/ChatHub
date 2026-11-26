using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

internal static class TestData
{
    public static UserProfile User(string displayName = "Alice") =>
        UserProfile.Create(AnyUsername(), displayName, "hash", email: Email.Create($"{Guid.NewGuid():N}@example.com"));

    public static Username AnyUsername(string displayName = "user") => Username.Create(TestUsernames.For(displayName));

    public static Attachment Png(Guid? fileId = null) => Attachment.Create(fileId ?? Guid.NewGuid(), "a.png", "image/png", 10);

    public static (Conversation Group, Guid Owner, Guid Member) GroupWithMember()
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var group = Conversation.CreateGroup(owner, "Team");
        group.AddMember(owner, member);
        group.ClearDomainEvents();
        return (group, owner, member);
    }

    public static (Conversation Group, Guid Owner, Guid Admin, Guid Member) GroupWithAdminAndMember()
    {
        var (group, owner, admin) = GroupWithMember();
        group.ChangeMemberRole(owner, admin, GroupRole.Admin);
        var member = Guid.NewGuid();
        group.AddMember(owner, member);
        group.ClearDomainEvents();
        return (group, owner, admin, member);
    }

    public static Message TextAfter(Message? previous, Guid conversationId, Guid senderId, string content = "hello")
    {
        if (previous is not null) SpinWait.SpinUntil(() => DateTime.UtcNow > previous.CreatedAtUtc);
        return Message.Create(conversationId, senderId, MessageType.Text, content);
    }
}
