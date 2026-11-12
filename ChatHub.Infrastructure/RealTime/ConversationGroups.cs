namespace ChatHub.Infrastructure.RealTime;

public static class ConversationGroups
{
    public static string Name(Guid conversationId) => $"conversation-{conversationId:D}";
}
