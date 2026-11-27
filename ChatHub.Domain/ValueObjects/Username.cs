using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.ValueObjects;

public sealed class Username : ValueObject
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    public const string RulesMessage =
        "Use 3-30 characters: lowercase letters, digits, underscores and dots. Start with a letter, " +
        "do not put two dots or underscores in a row, and do not end with a dot.";

    public const string ReservedMessage = "This username is reserved.";

    public static readonly FrozenSet<string> ReservedNames = new[]
    {
        "admin", "administrator", "support", "chathub", "system", "api", "root", "help", "guest", "bot", "null",
        "undefined", "moderator", "staff", "official", "security", "everyone", "anonymous", "deleted", "me",
        "settings", "www", "mail", "noreply", "postmaster", "abuse", "webmaster"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> ReservedPrefixes = ["guest_", "demo_", "chathub_"];

    private Username(string value) => Value = value;

    public string Value { get; }

    public static Username Create(string username) =>
        TryCreate(username, out var created, out var error) ? created : throw new DomainRuleViolationException(error);

    public static bool TryCreate(string? username, [NotNullWhen(true)] out Username? created, out string error)
    {
        created = null;
        var normalized = Normalize(username);
        if (!IsWellFormed(normalized))
        {
            error = RulesMessage;
            return false;
        }

        if (IsReservedNormalized(normalized))
        {
            error = ReservedMessage;
            return false;
        }

        created = new Username(normalized);
        error = string.Empty;
        return true;
    }

    public static Username ForSystemAccount(string username)
    {
        var normalized = Normalize(username);
        if (!IsWellFormed(normalized)) throw new DomainRuleViolationException(RulesMessage);
        return new Username(normalized);
    }

    public static Username FromStorage(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        return new Username(stored);
    }

    public static string Normalize(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsReserved(string? username) => IsReservedNormalized(Normalize(username));

    private static bool IsReservedNormalized(string normalized) =>
        ReservedNames.Contains(normalized) || ReservedPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal));

    public static bool IsWellFormed(string normalized)
    {
        if (normalized.Length is < MinLength or > MaxLength || !char.IsAsciiLetterLower(normalized[0]) || normalized[^1] == '.')
            return false;

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) continue;
            if (c is not ('_' or '.') || IsSeparator(normalized[i - 1])) return false;
        }

        return true;
    }

    private static bool IsSeparator(char c) => c is '_' or '.';

    public override string ToString() => Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
