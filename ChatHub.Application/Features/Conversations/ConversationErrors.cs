using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Conversations;

public static class ConversationErrors
{
    public static readonly Error NotFound =
        Error.NotFound("Conversation.NotFound", "The conversation was not found.");

    public static readonly Error NotMember =
        Error.Forbidden("Conversation.NotMember", "You are not an active member of this conversation.");

    public static readonly Error CannotChatWithSelf =
        Error.Validation("Conversation.CannotChatWithSelf", "You cannot start a conversation with yourself.");

    public static readonly Error Blocked =
        Error.Forbidden("Conversation.Blocked", "This action is not possible because one of you blocked the other.");
}
