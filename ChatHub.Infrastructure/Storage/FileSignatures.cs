namespace ChatHub.Infrastructure.Storage;

internal static class FileSignatures
{
    public const int HeaderLength = 16;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Ebml = [0x1A, 0x45, 0xDF, 0xA3];

    private static readonly Dictionary<string, Func<ReadOnlyMemory<byte>, bool>> Matchers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = h => StartsWith(h, Png),
            ["image/jpeg"] = h => StartsWith(h, Jpeg),
            ["image/gif"] = h => StartsWith(h, "GIF87a"u8) || StartsWith(h, "GIF89a"u8),
            ["image/webp"] = h => IsRiff(h, "WEBP"u8),
            ["application/pdf"] = h => StartsWith(h, Pdf),
            ["application/zip"] = IsZip,
            ["application/x-zip-compressed"] = IsZip,
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = IsZip,
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = IsZip,
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = IsZip,
            ["application/msword"] = h => StartsWith(h, Ole),
            ["application/vnd.ms-excel"] = h => StartsWith(h, Ole),
            ["application/vnd.ms-powerpoint"] = h => StartsWith(h, Ole),
            ["audio/mpeg"] = h => StartsWith(h, "ID3"u8) || IsMpegFrameSync(h),
            ["audio/aac"] = h => StartsWith(h, "ID3"u8) || IsAdtsSync(h),
            ["audio/ogg"] = h => StartsWith(h, "OggS"u8),
            ["audio/wav"] = h => IsRiff(h, "WAVE"u8),
            ["audio/x-wav"] = h => IsRiff(h, "WAVE"u8),
            ["audio/webm"] = h => StartsWith(h, Ebml),
            ["audio/mp4"] = h => h.Length >= 8 && h.Span[4..8].SequenceEqual("ftyp"u8)
        };

    public static bool Matches(string contentType, ReadOnlyMemory<byte> header) =>
        Matchers.TryGetValue(contentType, out var matches) && matches(header);

    private static bool StartsWith(ReadOnlyMemory<byte> header, ReadOnlySpan<byte> signature) =>
        header.Span.StartsWith(signature);

    private static bool IsRiff(ReadOnlyMemory<byte> header, ReadOnlySpan<byte> format) =>
        header.Length >= 12 && header.Span.StartsWith("RIFF"u8) && header.Span[8..12].SequenceEqual(format);

    private static bool IsZip(ReadOnlyMemory<byte> header)
    {
        var h = header.Span;
        return h.Length >= 4 && h[0] == 0x50 && h[1] == 0x4B &&
               ((h[2] == 0x03 && h[3] == 0x04) || (h[2] == 0x05 && h[3] == 0x06) || (h[2] == 0x07 && h[3] == 0x08));
    }

    private static bool IsMpegFrameSync(ReadOnlyMemory<byte> header) =>
        header.Length >= 2 && header.Span[0] == 0xFF && (header.Span[1] & 0xE0) == 0xE0;

    private static bool IsAdtsSync(ReadOnlyMemory<byte> header) =>
        header.Length >= 2 && header.Span[0] == 0xFF && (header.Span[1] & 0xF6) == 0xF0;
}
