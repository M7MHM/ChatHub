using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.ValueObjects;

public sealed class PhoneNumber : ValueObject
{
    private static readonly HashSet<string> ThreeDigitPrefixes =
    [
        "21", "22", "23", "24", "25", "26", "29", "35", "37", "38", "42",
        "50", "59", "67", "68", "69", "85", "88", "96", "97", "99"
    ];

    private PhoneNumber(string countryCode, string nationalNumber)
    {
        CountryCode = countryCode;
        NationalNumber = nationalNumber;
    }

    public string CountryCode { get; }
    public string NationalNumber { get; }
    public string FullNumber => $"+{CountryCode}{NationalNumber}";

    /// <summary>Creates a phone number from E.164 input (e.g. "+201001234567"). Spaces, dashes and parentheses are ignored.</summary>
    public static PhoneNumber Create(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new DomainRuleViolationException("Phone number is required.");

        var cleaned = new string(phone.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        if (!cleaned.StartsWith('+'))
            throw new DomainRuleViolationException("Phone number must start with '+' and a country code.");

        var digits = cleaned[1..];
        if (digits.Length is < 8 or > 15 || !digits.All(char.IsAsciiDigit) || digits[0] == '0')
            throw new DomainRuleViolationException("Phone number must contain 8 to 15 digits in E.164 format.");

        var codeLength = digits[0] is '1' or '7' ? 1 : ThreeDigitPrefixes.Contains(digits[..2]) ? 3 : 2;
        return new PhoneNumber(digits[..codeLength], digits[codeLength..]);
    }

    public override string ToString() => FullNumber;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return CountryCode;
        yield return NationalNumber;
    }
}
