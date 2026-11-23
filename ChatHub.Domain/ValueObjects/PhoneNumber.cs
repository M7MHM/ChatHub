using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;
using PhoneNumbers;
using LibPhoneNumber = PhoneNumbers.PhoneNumber;

namespace ChatHub.Domain.ValueObjects;

public sealed class PhoneNumber : ValueObject
{
    public const string DefaultRegion = "EG";
    public const string InvalidMessage = "Enter a valid Egyptian mobile number, e.g. 01001234567";

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    private PhoneNumber(string fullNumber, string countryCode, string nationalNumber, bool isValid)
    {
        FullNumber = fullNumber;
        CountryCode = countryCode;
        NationalNumber = nationalNumber;
        IsValid = isValid;
    }

    public string FullNumber { get; }
    public string CountryCode { get; }
    public string NationalNumber { get; }
    public bool IsValid { get; }

    public static PhoneNumber Create(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new DomainRuleViolationException("Phone number is required.");

        if (!TryParse(phone, out var parsed) || !IsMobile(parsed))
            throw new DomainRuleViolationException(InvalidMessage);

        return From(parsed, isValid: true);
    }

    public static PhoneNumber FromStorage(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);

        if (TryParse(stored, out var parsed) && Util.IsValidNumber(parsed))
        {
            var normalized = From(parsed, IsMobile(parsed));
            if (normalized.FullNumber == stored) return normalized;
        }

        var digits = stored.TrimStart('+');
        return new PhoneNumber(stored, string.Empty, digits, isValid: false);
    }

    private static bool TryParse(string phone, out LibPhoneNumber parsed)
    {
        parsed = null!;
        var trimmed = phone.Trim();
        if (trimmed.Length > 32 || !trimmed.All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '(' or ')' or '.' or '+'))
            return false;

        try
        {
            parsed = Util.Parse(trimmed, DefaultRegion);
            return true;
        }
        catch (NumberParseException)
        {
            return false;
        }
    }

    private static bool IsMobile(LibPhoneNumber parsed) =>
        Util.IsValidNumber(parsed) &&
        Util.GetNumberType(parsed) is PhoneNumberType.MOBILE or PhoneNumberType.FIXED_LINE_OR_MOBILE;

    private static PhoneNumber From(LibPhoneNumber parsed, bool isValid) =>
        new(Util.Format(parsed, PhoneNumberFormat.E164),
            parsed.CountryCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Util.GetNationalSignificantNumber(parsed),
            isValid);

    public override string ToString() => FullNumber;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return FullNumber;
    }
}
