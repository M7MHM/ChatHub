namespace ChatHub.Application.Features.Bot;

public sealed class BotSettings
{
    public const string SectionName = "Bot";

    public bool Enabled { get; init; } = true;
    public int ReplyDelayMilliseconds { get; init; } = 1500;
    public int MaxRepliesPerConversationPerMinute { get; init; } = 6;
    public int PollIntervalMilliseconds { get; init; } = 1000;

    public TimeSpan ReplyDelay => TimeSpan.FromMilliseconds(ReplyDelayMilliseconds);
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
    public static TimeSpan FloodWindow => TimeSpan.FromMinutes(1);
    public static TimeSpan MaxLateness => TimeSpan.FromMinutes(1);
}
