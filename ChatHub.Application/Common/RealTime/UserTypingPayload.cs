namespace ChatHub.Application.Common.RealTime;

public sealed record UserTypingPayload(Guid ConversationId, Guid UserId);
