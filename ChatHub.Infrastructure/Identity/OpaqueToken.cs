using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ChatHub.Infrastructure.Identity;

public static class OpaqueToken
{
    public const int SizeBytes = 32;
    public const int MaxHashLength = 128;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SizeBytes));

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
