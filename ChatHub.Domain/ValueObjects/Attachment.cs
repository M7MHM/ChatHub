using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

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
        if (string.IsNullOrWhiteSpace(fileUrl))
            throw new DomainRuleViolationException("Attachment file url is required.");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new DomainRuleViolationException("Attachment file name is required.");
        if (string.IsNullOrWhiteSpace(contentType))
            throw new DomainRuleViolationException("Attachment content type is required.");
        if (fileSizeBytes <= 0)
            throw new DomainRuleViolationException("Attachment file size must be greater than zero.");

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
