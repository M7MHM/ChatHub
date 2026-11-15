namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed record MemberRemovedPayload(Guid ConversationId, Guid UserId, string Reason)
{
    public const string Kicked = nameof(Kicked);
    public const string Left = nameof(Left);
}
