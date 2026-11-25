using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Files;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Storage;

public class HmacFileUrlSigner(IOptions<FileStorageSettings> options, TimeProvider timeProvider) : IFileUrlSigner
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.SigningKey);

    public string Sign(Guid fileId, DateTime expiresAtUtc)
    {
        var expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var signature = Base64Url.EncodeToString(Compute(fileId, expires));
        return string.Create(CultureInfo.InvariantCulture, $"{FileUrls.PathFor(fileId)}?exp={expires}&sig={signature}");
    }

    public bool Verify(Guid fileId, long? expiresAtUnixSeconds, string? signature)
    {
        if (expiresAtUnixSeconds is not { } expires || string.IsNullOrEmpty(signature)) return false;
        if (expires <= timeProvider.GetUtcNow().ToUnixTimeSeconds()) return false;

        byte[] provided;
        try
        {
            provided = Base64Url.DecodeFromChars(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(provided, Compute(fileId, expires));
    }

    private byte[] Compute(Guid fileId, long expires) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{fileId:N}.{expires}")));
}
