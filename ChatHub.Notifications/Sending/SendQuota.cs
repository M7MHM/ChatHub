using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Sending;

public sealed record SendUsage(
    DateTime AtUtc,
    int LastMinute,
    int DigestsLastMinute,
    int Today,
    int DigestsToday,
    DateTime? OldestInLastMinuteUtc)
{
    public int TransactionalToday => Today - DigestsToday;
}

public sealed class SendBudget
{
    private readonly NotificationSettings _settings;
    private int _minute;
    private int _digestMinute;
    private int _day;
    private int _digestDay;

    public SendBudget(NotificationSettings settings, SendUsage usage)
    {
        _settings = settings;
        Usage = usage;
        _minute = settings.MaxEmailsPerMinute - usage.LastMinute;
        _digestMinute = settings.MaxDigestsPerMinute - usage.DigestsLastMinute;
        _day = settings.MaxEmailsPerDay - usage.Today;
        _digestDay = settings.MaxDigestsPerDay - usage.DigestsToday;
    }

    public SendUsage Usage { get; }

    public int Transactional => Math.Max(0, Math.Min(_minute, _day));

    public int Digests => Math.Max(0, Math.Min(Math.Min(_minute, _digestMinute), Math.Min(_day, _digestDay)));

    public void Spend(NotificationKind kind)
    {
        _minute--;
        _day--;
        if (kind != NotificationKind.Digest) return;
        _digestMinute--;
        _digestDay--;
    }

    public string? ExhaustedLimit(NotificationKind kind) => kind switch
    {
        _ when _day <= 0 => $"{_settings.MaxEmailsPerDay} emails per UTC day",
        _ when _minute <= 0 => $"{_settings.MaxEmailsPerMinute} emails per minute",
        NotificationKind.Digest when _digestDay <= 0 => $"{_settings.MaxDigestsPerDay} digests per UTC day",
        NotificationKind.Digest when _digestMinute <= 0 => $"{_settings.MaxDigestsPerMinute} digests per minute",
        _ => null
    };

    public bool IsDayLimit(NotificationKind kind) =>
        _day <= 0 || (kind == NotificationKind.Digest && _digestDay <= 0);

    public DateTime NextCapacityAtUtc(NotificationKind kind) =>
        IsDayLimit(kind)
            ? Usage.AtUtc.Date.AddDays(1)
            : (Usage.OldestInLastMinuteUtc ?? Usage.AtUtc) + TimeSpan.FromMinutes(1);
}

public sealed class SendQuota(IOptions<NotificationSettings> options)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private long _skipped;

    public long SkippedSuppressed => Interlocked.Read(ref _skipped);

    public void CountSkipped() => Interlocked.Increment(ref _skipped);

    public SendBudget Budget(SendUsage usage) => new(options.Value, usage);

    public async Task<SendUsage> GetUsageAsync(NotificationsDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var minuteAgo = nowUtc - Minute;
        var dayStart = nowUtc.Date;
        var from = minuteAgo < dayStart ? minuteAgo : dayStart;

        var row = await db.Database.SqlQuery<UsageRow>($"""
                SELECT count(*) FILTER (WHERE "SentAtUtc" > {minuteAgo})::int AS "LastMinute",
                       count(*) FILTER (WHERE "SentAtUtc" > {minuteAgo} AND "Kind" = 'Digest')::int AS "DigestsLastMinute",
                       count(*) FILTER (WHERE "SentAtUtc" >= {dayStart})::int AS "Today",
                       count(*) FILTER (WHERE "SentAtUtc" >= {dayStart} AND "Kind" = 'Digest')::int AS "DigestsToday",
                       min("SentAtUtc") FILTER (WHERE "SentAtUtc" > {minuteAgo}) AS "OldestInLastMinuteUtc"
                FROM "SentNotifications"
                WHERE "SentAtUtc" >= {from}
                """)
            .SingleAsync(cancellationToken);

        return new SendUsage(nowUtc, row.LastMinute, row.DigestsLastMinute, row.Today, row.DigestsToday, row.OldestInLastMinuteUtc);
    }

    private sealed record UsageRow(int LastMinute, int DigestsLastMinute, int Today, int DigestsToday, DateTime? OldestInLastMinuteUtc);
}
