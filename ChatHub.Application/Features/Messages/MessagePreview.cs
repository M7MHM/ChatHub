using ChatHub.Domain.Enums;

namespace ChatHub.Application.Features.Messages;

internal static class MessagePreview
{
    public const int MaxLength = 100;
    public const string DeletedText = "This message was deleted";
    private const string Ellipsis = "…";

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

    public static string SingleLine(MessageType type, string? content, int maxLength)
    {
        var text = string.Join(' ', Text(type, false, content).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= maxLength) return text;

        var cut = maxLength - Ellipsis.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + Ellipsis;
    }
}
