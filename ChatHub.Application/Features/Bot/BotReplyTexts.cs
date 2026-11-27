using System.Text.RegularExpressions;

namespace ChatHub.Application.Features.Bot;

public static partial class BotReplyTexts
{
    public const string Greeting =
        "Hi there! 👋 I'm ChatHub Bot. Type \"help\" to see what I can do, or \"features\" for a quick tour.";

    public const string Help =
        "Here's what you can try: open the Weekend plans group, reply to a message, edit or delete one, start a chat " +
        "with Layla, Omar, Sara or Youssef, or hide your last seen in the privacy settings. Type \"features\" for the tour. " +
        "Guest sessions and everything in them are deleted after 24 hours.";

    public const string FeatureTour =
        "Quick tour of ChatHub ✨\n" +
        "• Real-time messages with delivered and read receipts ✓✓\n" +
        "• Replies, edits and delete for everyone\n" +
        "• Photos, files and voice notes\n" +
        "• Groups with owners and admins\n" +
        "• Typing indicators, online status and last-seen privacy\n" +
        "• Email digests of unread messages for registered users";

    public static readonly IReadOnlyList<string> SmallTalk =
    [
        "Interesting! Tell me more 🙂",
        "I'm only a demo bot, but your message really went through ChatHub's real-time pipeline ⚡",
        "Have you seen the Weekend plans group? They're planning a trip 🏖️",
        "Tip: you can reply to any message, and edit or delete your own.",
        "Fun fact: every message here reaches all server instances through Redis and SignalR."
    ];

    public static string For(string? text, int previousReplies)
    {
        var normalized = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("feature") || normalized.Contains("tour")) return FeatureTour;
        if (HelpPattern().IsMatch(normalized)) return Help;
        if (GreetingPattern().IsMatch(normalized)) return Greeting;
        return SmallTalk[Math.Abs(previousReplies) % SmallTalk.Count];
    }

    [GeneratedRegex(@"\bhelp\b")]
    private static partial Regex HelpPattern();

    [GeneratedRegex(@"\b(hi|hello|hey|hiya|salam|marhaba|مرحبا|أهلا|اهلا|السلام)\b")]
    private static partial Regex GreetingPattern();
}
