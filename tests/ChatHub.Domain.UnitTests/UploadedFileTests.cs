using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class UploadedFileTests
{
    private readonly Guid _ownerId = Guid.NewGuid();

    [Fact]
    public void Create_MixedCaseContentType_IsStoredLowercaseWithTrimmedValues()
    {
        var file = UploadedFile.Create(_ownerId, FilePurpose.MessageAttachment, " 2026/11/x.png ", " x.png ", "IMAGE/PNG", 10);

        file.ContentType.ShouldBe("image/png");
        file.StorageKey.ShouldBe("2026/11/x.png");
        file.FileName.ShouldBe("x.png");
        file.AttachedAtUtc.ShouldBeNull();
        file.IsDeleted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(FilePurpose.Avatar)]
    [InlineData(FilePurpose.GroupImage)]
    public void Create_AvatarOrGroupImageThatIsNotAnImage_Throws(FilePurpose purpose)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UploadedFile.Create(_ownerId, purpose, "k", "a.pdf", "application/pdf", 10));
    }

    [Fact]
    public void Create_EmptyOwnerId_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UploadedFile.Create(Guid.Empty, FilePurpose.MessageAttachment, "k", "x.png", "image/png", 10));
    }

    [Fact]
    public void Create_UndefinedPurpose_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UploadedFile.Create(_ownerId, (FilePurpose)42, "k", "x.png", "image/png", 10));
    }

    [Theory]
    [InlineData(" ", "x.png", "image/png")]
    [InlineData("k", " ", "image/png")]
    [InlineData("k", "x.png", " ")]
    public void Create_BlankRequiredValue_Throws(string storageKey, string fileName, string contentType)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UploadedFile.Create(_ownerId, FilePurpose.MessageAttachment, storageKey, fileName, contentType, 10));
    }

    [Theory]
    [InlineData(UploadedFile.MaxStorageKeyLength + 1, 1, 1)]
    [InlineData(1, UploadedFile.MaxFileNameLength + 1, 1)]
    [InlineData(1, 1, UploadedFile.MaxContentTypeLength + 1)]
    public void Create_ValueLongerThanItsMaximum_Throws(int keyLength, int nameLength, int typeLength)
    {
        Should.Throw<DomainRuleViolationException>(() => UploadedFile.Create(_ownerId, FilePurpose.MessageAttachment,
            new string('k', keyLength), new string('n', nameLength), new string('t', typeLength), 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_NonPositiveSize_Throws(long sizeBytes)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UploadedFile.Create(_ownerId, FilePurpose.MessageAttachment, "k", "x.png", "image/png", sizeBytes));
    }

    [Fact]
    public void MarkAttached_UnattachedFile_RecordsTheAttachTime()
    {
        var file = NewAttachment();

        file.MarkAttached();

        file.AttachedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public void MarkAttached_SecondTime_Throws()
    {
        var file = NewAttachment();
        file.MarkAttached();

        Should.Throw<DomainRuleViolationException>(file.MarkAttached);
    }

    [Fact]
    public void MarkAttached_DeletedFile_Throws()
    {
        var file = NewAttachment();
        file.MarkDeleted();

        Should.Throw<DomainRuleViolationException>(file.MarkAttached);
    }

    [Fact]
    public void MarkDeleted_NewFile_SetsTheDeletedFlagAndTime()
    {
        var file = NewAttachment();

        file.MarkDeleted();

        file.IsDeleted.ShouldBeTrue();
        file.DeletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public void MarkDeleted_CalledTwice_KeepsTheFirstDeletionTime()
    {
        var file = NewAttachment();
        file.MarkAttached();
        file.MarkDeleted();
        var firstDeletedAt = file.DeletedAtUtc;
        SpinWait.SpinUntil(() => DateTime.UtcNow > firstDeletedAt);

        file.MarkDeleted();

        file.DeletedAtUtc.ShouldBe(firstDeletedAt);
    }

    private UploadedFile NewAttachment() =>
        UploadedFile.Create(_ownerId, FilePurpose.MessageAttachment, "2026/11/x.png", "x.png", "image/png", 10);
}
