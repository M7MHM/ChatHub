using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Application.Common.Validation;
public static class ContactParser
{
    public static Email? TryParseEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Email.Create(value);
        }
        catch (DomainRuleViolationException)
        {
            return null;
        }
    }

    public static PhoneNumber? TryParsePhoneNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return PhoneNumber.Create(value);
        }
        catch (DomainRuleViolationException)
        {
            return null;
        }
    }
}
