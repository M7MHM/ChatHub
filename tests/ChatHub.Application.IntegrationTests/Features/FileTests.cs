using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Files.Queries;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class FileTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task UploadFile_MessageAttachment_ReturnsAFileResponseWithASignedUrl()
    {
        var yara = await CreateUserAsync("Yara");

        var file = (await TryUploadAsync(yara, FilePurpose.MessageAttachment, TestFiles.Png, "photo.png", "image/png")).ShouldSucceed();

        file.FileName.ShouldBe("photo.png");
        file.ContentType.ShouldBe("image/png");
        file.SizeBytes.ShouldBe(TestFiles.Png.Length);
        file.Url.ShouldStartWith($"/api/files/{file.Id}?exp=");
        file.Url.ShouldContain("&sig=");
    }

    [Fact]
    public async Task UploadFile_Avatar_ReturnsThePublicFileUrl()
    {
        var yara = await CreateUserAsync("Yara");

        var file = await UploadPngAsync(yara, FilePurpose.Avatar, "me.png");

        file.Url.ShouldBe($"/api/files/{file.Id}");
    }

    [Fact]
    public async Task UploadFile_EmptyFile_ThrowsValidationException()
    {
        var paula = await CreateUserAsync("Paula");

        var exception = await Should.ThrowAsync<ValidationException>(() =>
            TryUploadAsync(paula, FilePurpose.MessageAttachment, [], "e.png", "image/png"));

        exception.Errors.ShouldContainKey("File");
    }

    [Fact]
    public async Task UploadFile_PdfBytesDeclaredAsPng_ThrowsValidationException()
    {
        var yara = await CreateUserAsync("Yara");

        var exception = await Should.ThrowAsync<ValidationException>(() =>
            TryUploadAsync(yara, FilePurpose.MessageAttachment, TestFiles.Pdf, "fake.png", "image/png"));

        exception.Errors["File"].ShouldContain("File content does not match its type");
    }

    [Fact]
    public async Task UploadFile_NonImageAvatar_ThrowsValidationException()
    {
        var yara = await CreateUserAsync("Yara");

        var exception = await Should.ThrowAsync<ValidationException>(() =>
            TryUploadAsync(yara, FilePurpose.Avatar, TestFiles.Pdf, "a.pdf", "application/pdf"));

        exception.Errors.ShouldContainKey("File");
    }

    [Fact]
    public async Task UploadFile_Success_StoresARowOwnedByTheCallerAndBytesUnderThePrivateRoot()
    {
        var yara = await CreateUserAsync("Yara");

        var file = await UploadPngAsync(yara, fileName: "photo.png");

        var stored = await QueryDbAsync(db => db.UploadedFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id));
        stored.OwnerId.ShouldBe(yara);
        stored.AttachedAtUtc.ShouldBeNull();
        File.Exists(Path.Combine(FilesRoot, stored.StorageKey)).ShouldBeTrue();
        stored.StorageKey.ShouldNotContain("uploads");
    }

    [Fact]
    public async Task SendMessage_ImageAttachmentFileId_ResponseCarriesASignedUrl()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();
        var file = await UploadPngAsync(yara, fileName: "photo.png");

        var message = await SendFileAsync(yara, direct, MessageType.Image, file.Id);

        var attachment = message.Attachment.ShouldNotBeNull();
        attachment.FileId.ShouldBe(file.Id);
        attachment.FileName.ShouldBe("photo.png");
        attachment.Url.ShouldContain("sig=");
    }

    [Fact]
    public async Task SendMessage_ImageAttachmentFileId_MarksTheFileAttached()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();
        var file = await UploadPngAsync(yara);

        await SendFileAsync(yara, direct, MessageType.Image, file.Id);

        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == file.Id && f.AttachedAtUtc != null))).ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_SomeoneElsesFile_ReturnsNotOwned()
    {
        var (yara, zane, _, direct) = await CreateFileUsersAsync();
        var zanesFile = await UploadPngAsync(zane);

        var result = await TrySendAsync(yara, direct, MessageType.Image, null, zanesFile.Id);

        result.ShouldFailWith(FileErrors.NotOwned);
    }

    [Fact]
    public async Task SendMessage_AlreadyAttachedFile_ReturnsAlreadyAttached()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();
        var file = await UploadPngAsync(yara);
        await SendFileAsync(yara, direct, MessageType.Image, file.Id);

        var result = await TrySendAsync(yara, direct, MessageType.Image, null, file.Id);

        result.ShouldFailWith(FileErrors.AlreadyAttached);
    }

    [Fact]
    public async Task SendMessage_AvatarFileAsAttachment_ReturnsWrongPurpose()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();
        var avatar = await UploadPngAsync(yara, FilePurpose.Avatar);

        var result = await TrySendAsync(yara, direct, MessageType.Image, null, avatar.Id);

        result.ShouldFailWith(FileErrors.WrongPurpose);
    }

    [Fact]
    public async Task SendMessage_UnknownFileId_ReturnsFileNotFound()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();

        var result = await TrySendAsync(yara, direct, MessageType.File, null, Guid.NewGuid());

        result.ShouldFailWith(FileErrors.NotFound);
    }

    [Fact]
    public async Task SendMessage_VoiceWithAnAudioFile_Succeeds()
    {
        var (yara, _, _, direct) = await CreateFileUsersAsync();
        var ogg = await UploadAsync(yara, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");

        var result = await TrySendAsync(yara, direct, MessageType.Voice, null, ogg.Id);

        result.ShouldSucceed();
    }

    [Fact]
    public async Task GetFileForDownload_Owner_Succeeds()
    {
        var (yara, _, _, _, image) = await CreateSentImageAsync();

        (await DownloadAsync(yara, image.FileId)).ShouldSucceed();
    }

    [Fact]
    public async Task GetFileForDownload_MemberWhoCanSeeTheMessage_Succeeds()
    {
        var (_, zane, _, _, image) = await CreateSentImageAsync();

        (await DownloadAsync(zane, image.FileId)).ShouldSucceed();
    }

    [Fact]
    public async Task GetFileForDownload_NonMember_ReturnsNotFound()
    {
        var (_, _, ivan, _, image) = await CreateSentImageAsync();

        (await DownloadAsync(ivan, image.FileId)).ShouldFailWith(FileErrors.NotFound);
    }

    [Fact]
    public async Task GetFileForDownload_AnonymousWithAValidSignature_Succeeds()
    {
        var (_, _, _, _, image) = await CreateSentImageAsync();
        var (exp, sig) = SignedParts(image.Url);

        (await DownloadAsync(null, image.FileId, exp, sig)).ShouldSucceed();
    }

    [Theory]
    [InlineData("tampered signature")]
    [InlineData("changed expiry")]
    [InlineData("no signature")]
    public async Task GetFileForDownload_AnonymousWithoutAValidSignature_ReturnsNotFound(string variant)
    {
        var (_, _, _, _, image) = await CreateSentImageAsync();
        var (exp, sig) = SignedParts(image.Url);
        var tamperedSig = (sig[0] == 'A' ? "B" : "A") + sig[1..];

        var result = variant switch
        {
            "tampered signature" => await DownloadAsync(null, image.FileId, exp, tamperedSig),
            "changed expiry" => await DownloadAsync(null, image.FileId, exp + 60, sig),
            _ => await DownloadAsync(null, image.FileId)
        };

        result.ShouldFailWith(FileErrors.NotFound);
    }

    [Fact]
    public async Task GetFileForDownload_ExpiredSignature_ReturnsNotFound()
    {
        var (_, _, _, _, image) = await CreateSentImageAsync();
        var (exp, sig) = SignedParts(image.Url);

        Clock.Advance(FileUrls.SignedUrlLifetime + TimeSpan.FromMinutes(1));

        (await DownloadAsync(null, image.FileId, exp, sig)).ShouldFailWith(FileErrors.NotFound);
    }

    [Fact]
    public async Task GetFileForDownload_AvatarAnonymously_Succeeds()
    {
        var yara = await CreateUserAsync("Yara");
        var avatar = await UploadPngAsync(yara, FilePurpose.Avatar, "me.png");

        (await DownloadAsync(null, avatar.Id)).ShouldSucceed();
    }

    [Fact]
    public async Task GetFileForDownload_MessageHiddenByClear_ReturnsNotFoundForThatMember()
    {
        var (_, zane, _, direct, image) = await CreateSentImageAsync();
        await PauseAsync();

        await ClearChatAsync(zane, direct);

        (await DownloadAsync(zane, image.FileId)).ShouldFailWith(FileErrors.NotFound);
    }

    [Fact]
    public async Task UpdateMyProfile_AvatarFile_SetsTheAvatarUrl()
    {
        var yara = await CreateUserAsync("Yara");
        var avatar = await UploadPngAsync(yara, FilePurpose.Avatar, "me.png");

        (await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, avatar.Id))).ShouldSucceed();

        (await SendAsync(yara, new GetMeQuery())).ShouldSucceed().AvatarUrl.ShouldBe($"/api/files/{avatar.Id}");
    }

    [Fact]
    public async Task UpdateMyProfile_ReplacingTheAvatar_MarksTheOldFileDeleted()
    {
        var yara = await CreateUserAsync("Yara");
        var first = await UploadPngAsync(yara, FilePurpose.Avatar, "me.png");
        (await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, first.Id))).ShouldSucceed();
        var second = await UploadPngAsync(yara, FilePurpose.Avatar, "me2.png");

        (await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, second.Id))).ShouldSucceed();

        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == first.Id && f.IsDeleted))).ShouldBeTrue();
        (await DownloadAsync(null, first.Id)).ShouldFailWith(FileErrors.NotFound);
        (await DownloadAsync(null, second.Id)).ShouldSucceed();
    }

    [Fact]
    public async Task UpdateMyProfile_RemoveAvatar_ClearsTheAvatarAndDeletesTheFile()
    {
        var yara = await CreateUserAsync("Yara");
        var avatar = await UploadPngAsync(yara, FilePurpose.Avatar, "me.png");
        (await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, avatar.Id))).ShouldSucceed();

        (await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, RemoveAvatar: true))).ShouldSucceed();

        (await SendAsync(yara, new GetMeQuery())).ShouldSucceed().AvatarUrl.ShouldBeNull();
        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == avatar.Id && f.IsDeleted))).ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateMyProfile_SomeoneElsesFileAsAvatar_ReturnsNotOwned()
    {
        var (yara, zane, _, _) = await CreateFileUsersAsync();
        var zanesFile = await UploadPngAsync(zane);

        var result = await SendAsync(yara, new UpdateMyProfileCommand("Yara", null, zanesFile.Id));

        result.ShouldFailWith(FileErrors.NotOwned);
    }

    [Fact]
    public async Task CreateGroup_WithImageFileId_SetsTheGroupImageUrl()
    {
        var (yara, zane, _, _) = await CreateFileUsersAsync();
        var image = await UploadPngAsync(yara, FilePurpose.GroupImage, "g.png");

        var group = (await SendAsync(yara, new CreateGroupCommand("Files", image.Id, [zane]))).ShouldSucceed().ConversationId;

        (await SendAsync(yara, new GetConversationDetailsQuery(group))).ShouldSucceed().ImageUrl.ShouldBe($"/api/files/{image.Id}");
    }

    [Fact]
    public async Task UpdateGroupInfo_NewImage_ReplacesTheImageAndDeletesTheOldFile()
    {
        var (yara, zane, _, _) = await CreateFileUsersAsync();
        var first = await UploadPngAsync(yara, FilePurpose.GroupImage, "g.png");
        var group = (await SendAsync(yara, new CreateGroupCommand("Files", first.Id, [zane]))).ShouldSucceed().ConversationId;
        var second = await UploadPngAsync(yara, FilePurpose.GroupImage, "g2.png");

        (await SendAsync(yara, new UpdateGroupInfoCommand("Files", second.Id) { ConversationId = group })).ShouldSucceed();

        (await SendAsync(yara, new GetConversationDetailsQuery(group))).ShouldSucceed().ImageUrl.ShouldBe($"/api/files/{second.Id}");
        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == first.Id && f.IsDeleted))).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteMessage_WithAnAttachment_MarksTheFileDeleted()
    {
        var (yara, _, _, _, image) = await CreateSentImageAsync();

        (await SendAsync(yara, new DeleteMessageCommand(image.MessageId))).ShouldSucceed();

        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == image.FileId && f.IsDeleted))).ShouldBeTrue();
    }

    private async Task<Result<FileDownload>> DownloadAsync(Guid? userId, Guid fileId, long? exp = null, string? sig = null)
    {
        var download = await SendAsync(userId, new GetFileForDownloadQuery(fileId, exp, sig));
        if (download.IsSuccess) await download.Value.Content.DisposeAsync();
        return download;
    }

    private static (long Exp, string Sig) SignedParts(string url)
    {
        var query = url[(url.IndexOf('?') + 1)..].Split('&').Select(part => part.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        return (long.Parse(query["exp"]), query["sig"]);
    }

    private async Task<(Guid Yara, Guid Zane, Guid Ivan, Guid Direct)> CreateFileUsersAsync()
    {
        var yara = await CreateUserAsync("Yara");
        var zane = await CreateUserAsync("Zane");
        var ivan = await CreateUserAsync("Ivan");
        return (yara, zane, ivan, await CreateDirectAsync(yara, zane));
    }

    private async Task<(Guid Yara, Guid Zane, Guid Ivan, Guid Direct, SentImage Image)> CreateSentImageAsync()
    {
        var (yara, zane, ivan, direct) = await CreateFileUsersAsync();
        var file = await UploadPngAsync(yara, fileName: "photo.png");
        var message = await SendFileAsync(yara, direct, MessageType.Image, file.Id);
        return (yara, zane, ivan, direct, new SentImage(message.Id, file.Id, message.Attachment!.Url));
    }

    private sealed record SentImage(Guid MessageId, Guid FileId, string Url);
}
