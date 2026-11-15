using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.ValueObjects;

public sealed class Attachment : ValueObject
{
    private Attachment(Guid fileId, string fileName, string contentType, long fileSizeBytes)
    {
        FileId = fileId;
        FileName = fileName;
        ContentType = contentType;
        FileSizeBytes = fileSizeBytes;
    }

    public Guid FileId { get; }
    public string FileName { get; }
    public string ContentType { get; }
    public long FileSizeBytes { get; }

    public static Attachment Create(Guid fileId, string fileName, string contentType, long fileSizeBytes)
    {
        if (fileId == Guid.Empty)
            throw new DomainRuleViolationException("Attachment file id is required.");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new DomainRuleViolationException("Attachment file name is required.");
        if (string.IsNullOrWhiteSpace(contentType))
            throw new DomainRuleViolationException("Attachment content type is required.");
        if (fileSizeBytes <= 0)
            throw new DomainRuleViolationException("Attachment file size must be greater than zero.");

        return new Attachment(fileId, fileName, contentType, fileSizeBytes);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return FileId;
        yield return FileName;
        yield return ContentType;
        yield return FileSizeBytes;
    }
}
