using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class UploadedFile : BaseEntity
{
    public const int MaxFileNameLength = 255;
    public const int MaxContentTypeLength = 255;
    public const int MaxStorageKeyLength = 512;

    private UploadedFile() { }

    public Guid OwnerId { get; private set; }
    public FilePurpose Purpose { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? AttachedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public static UploadedFile Create(Guid ownerId, FilePurpose purpose, string storageKey, string fileName,
        string contentType, long sizeBytes)
    {
        if (ownerId == Guid.Empty)
            throw new DomainRuleViolationException("File owner id is required.");
        if (!Enum.IsDefined(purpose))
            throw new DomainRuleViolationException("Invalid file purpose.");

        var key = Required(storageKey, MaxStorageKeyLength, "Storage key");
        var name = Required(fileName, MaxFileNameLength, "File name");
        var type = Required(contentType, MaxContentTypeLength, "Content type").ToLowerInvariant();
        if (sizeBytes <= 0)
            throw new DomainRuleViolationException("File size must be greater than zero.");
        if (purpose is FilePurpose.Avatar or FilePurpose.GroupImage && !type.StartsWith("image/", StringComparison.Ordinal))
            throw new DomainRuleViolationException("Avatars and group images must be images.");

        return new UploadedFile
        {
            OwnerId = ownerId,
            Purpose = purpose,
            StorageKey = key,
            FileName = name,
            ContentType = type,
            SizeBytes = sizeBytes
        };
    }

    public void MarkAttached()
    {
        if (IsDeleted)
            throw new DomainRuleViolationException("A deleted file cannot be attached.");
        if (AttachedAtUtc is not null)
            throw new DomainRuleViolationException("The file is already attached.");

        AttachedAtUtc = DateTime.UtcNow;
    }

    public void MarkDeleted()
    {
        if (IsDeleted) return;
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
    }

    private static string Required(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException($"{name} is required.");
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new DomainRuleViolationException($"{name} cannot exceed {maxLength} characters.");
        return trimmed;
    }
}
