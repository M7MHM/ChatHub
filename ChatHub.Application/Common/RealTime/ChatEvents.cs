namespace ChatHub.Application.Common.RealTime;
public static class ChatEvents
{
    public const string MessageReceived = nameof(MessageReceived);
    public const string MessageUpdated = nameof(MessageUpdated);
    public const string MessageDeleted = nameof(MessageDeleted);
    public const string MessagesRead = nameof(MessagesRead);
    public const string MessagesDelivered = nameof(MessagesDelivered);
    public const string UserTyping = nameof(UserTyping);
    public const string ConversationCreated = nameof(ConversationCreated);
    public const string ConversationUpdated = nameof(ConversationUpdated);
    public const string MemberAdded = nameof(MemberAdded);
    public const string MemberRemoved = nameof(MemberRemoved);
    public const string MemberRoleChanged = nameof(MemberRoleChanged);
    public const string PresenceChanged = nameof(PresenceChanged);
    public const string ProfileUpdated = nameof(ProfileUpdated);
}
