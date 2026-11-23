using ChatHub.Notifications.Digests;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Sending;

public sealed class EmailSuppression
{
    private readonly IReadOnlyList<string> _domains;

    public EmailSuppression(IOptions<NotificationSettings> options, IHostEnvironment? environment = null)
    {
        var configured = options.Value.SuppressedEmailDomains;
        _domains = configured is not null
            ? Parse(configured)
            : environment?.IsDevelopment() == true ? [] : Parse(NotificationSettings.ReservedEmailDomains);
    }

    public IReadOnlyList<string> Domains => _domains;

    public bool IsSuppressed(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || _domains.Count == 0) return false;
        var at = address.LastIndexOf('@');
        if (at < 0) return false;

        var domain = address[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        return _domains.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
    }

    private static string[] Parse(string domains) =>
        [.. domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.TrimStart('.').ToLowerInvariant())];
}
