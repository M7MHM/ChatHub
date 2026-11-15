using ChatHub.Domain.Enums;

namespace ChatHub.Application.Features.Messages;

internal static class MessagePreview
{
    public const int MaxLength = 100;
    public const string DeletedText = "This message was deleted";

    public static string Text(MessageType type, bool isDeleted, string? content) =>
        isDeleted
            ? DeletedText
            : type switch
            {
                MessageType.Image => "[Image]",
                MessageType.File => "[File]",
                MessageType.Voice => "[Voice]",
                _ => content ?? string.Empty
            };
}
