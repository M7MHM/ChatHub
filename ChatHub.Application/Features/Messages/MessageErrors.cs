using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Messages;

public static class MessageErrors
{
    public static readonly Error NotFound = Error.NotFound("Message.NotFound", "The message was not found.");

    public static readonly Error ReplyTargetNotFound =
        Error.NotFound("Message.ReplyTargetNotFound", "The message you are replying to was not found.");
}
