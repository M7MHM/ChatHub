namespace ChatHub.Tests;

public static class TestUsernames
{
    public static string For(string displayName)
    {
        var mapped = displayName.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '_');
        var slug = string.Join('_', new string([.. mapped]).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0 || !char.IsAsciiLetterLower(slug[0])) slug = "u" + slug;
        if (slug.Length > 20) slug = slug[..20].TrimEnd('_');
        return $"{slug}_{Guid.NewGuid().ToString("N")[..6]}";
    }
}
