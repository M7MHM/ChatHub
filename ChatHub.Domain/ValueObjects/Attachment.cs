using ChatHub.Domain.Common;

namespace ChatHub.Domain.ValueObjects;

public sealed class Attachment : ValueObject
{
    private Attachment(string fileUrl, string fileName, string contentType, long fileSizeBytes)
    {
        FileUrl = fileUrl;
        FileName = fileName;
        ContentType = contentType;
        FileSizeBytes = fileSizeBytes;
    }

    public string FileUrl { get; }
    public string FileName { get; }
    public string ContentType { get; }
    public long FileSizeBytes { get; }

    public static Attachment Create(string fileUrl, string fileName, string contentType, long fileSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeBytes);

        return new Attachment(fileUrl, fileName, contentType, fileSizeBytes);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return FileUrl;
        yield return FileName;
        yield return ContentType;
        yield return FileSizeBytes;
    }
}
