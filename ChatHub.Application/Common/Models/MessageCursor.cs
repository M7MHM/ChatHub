using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace ChatHub.Application.Common.Models;

public static class MessageCursor
{
    public static string Encode(DateTime createdAtUtc, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{createdAtUtc.Ticks}_{id:N}")));

    public static bool TryDecode(string? cursor, out DateTime createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('_');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks > DateTime.MaxValue.Ticks ||
                !Guid.TryParseExact(parts[1], "N", out id))
                return false;

            createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
