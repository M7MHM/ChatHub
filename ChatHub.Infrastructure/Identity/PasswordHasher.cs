using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace ChatHub.Infrastructure.Identity;
public class PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<UserProfile> _inner = new();

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return _inner.HashPassword(null!, password);
    }

    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash)) return false;

        try
        {
            return _inner.VerifyHashedPassword(null!, passwordHash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
