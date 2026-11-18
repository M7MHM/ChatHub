using System.Net;
using System.Runtime.Versioning;
using System.Text;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Application.Common.Interfaces;
using ChatHub.WebAPI.Middlewares;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests;

public sealed class FileTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Upload_MessageAttachment_Returns201FileResponseWithSignedUrlAndLocation()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", alice.Token);

        var id = response.Json.G("id");
        response.Status.ShouldBe(201);
        response.Header("Location").ShouldBe($"/api/files/{id}");
        response.Json.S("url").ShouldNotBeNull().ShouldStartWith($"/api/files/{id}?exp=");
        response.Json.S("fileName").ShouldBe("pixel.png");
        response.Json.S("contentType").ShouldBe("image/png");
        response.Json.P("sizeBytes").GetInt64().ShouldBe(TestFiles.Png.Length);
    }

    [Fact]
    public async Task Upload_Success_StoresBytesPrivatelyWithoutStaticServing()
    {
        var alice = await RegisterAsync("Alice");
        var upload = await UploadPngAsync(alice);

        var key = await StorageKeyOfAsync(upload.G("id"));

        File.Exists(Path.Combine(Fixture.FilesRoot, key)).ShouldBeTrue();
        (await Api.Http.GetAsync($"/uploads/{key}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Api.Http.GetAsync($"/{key}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Api.Http.GetAsync("/uploads/2026/01/missing.png")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task Upload_StorageFolderNotWritable_Returns500ProblemWithoutLeakingDetails()
    {
        var alice = await RegisterAsync("Alice");
        var folder = await StorageFolderOfAsync(alice);

        ApiResponse denied;
        using (new DeniedWriteAccess(folder))
            denied = await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", alice.Token);

        TakeLoggedErrors();
        denied.Status.ShouldBe(500);
        denied.IsProblem.ShouldBeTrue();
        denied.Json.S("title").ShouldBe("An unexpected error occurred.");
        denied.Json.P("detail").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
        denied.Text.ShouldNotContain("Access to the path");
        denied.Text.ShouldNotContain("UnauthorizedAccess");
        denied.Header("WWW-Authenticate").ShouldBeNull();
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task Upload_StorageFolderWritableAgain_Returns201()
    {
        var alice = await RegisterAsync("Alice");
        var folder = await StorageFolderOfAsync(alice);
        using (new DeniedWriteAccess(folder))
            await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", alice.Token);
        TakeLoggedErrors();

        var response = await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", alice.Token);

        response.Status.ShouldBe(201);
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task Upload_StorageFolderNotWritable_LogsOneErrorWithTheAccessException()
    {
        var alice = await RegisterAsync("Alice");
        var folder = await StorageFolderOfAsync(alice);

        using (new DeniedWriteAccess(folder))
            await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", alice.Token);

        var error = TakeLoggedErrors().ShouldHaveSingleItem();
        error.Category.ShouldBe(typeof(GlobalExceptionHandler).FullName);
        error.Exception.ShouldBeOfType<UnauthorizedAccessException>().Message.ShouldContain("Access to the path");
    }

    [Fact]
    public async Task SendMessage_ImageAttachmentFileId_Returns201WithSignedAttachment()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var file = await UploadPngAsync(alice);

        var response = await SendImageAsync(alice, directId, file.G("id"));

        response.Status.ShouldBe(201);
        var attachment = response.Json.P("attachment");
        attachment.G("fileId").ShouldBe(file.G("id"));
        attachment.S("fileName").ShouldBe("pixel.png");
        attachment.S("url").ShouldNotBeNull().ShouldStartWith($"/api/files/{file.G("id")}?exp=");
        attachment.S("url").ShouldNotBeNull().ShouldContain("&sig=");
    }

    [Fact]
    public async Task Download_SignedUrlWithoutAToken_ReturnsTheInlineImagePrivatelyCached()
    {
        var (_, _, signedUrl, _, _) = await CreateSentImageAsync();

        using var response = await GetRawAsync(signedUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("image/png");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Content.Headers.ContentDisposition.ShouldBeNull();
        response.Headers.CacheControl.ShouldNotBeNull().Private.ShouldBeTrue();
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(TestFiles.Png);
    }

    [Fact]
    public async Task Download_TamperedSignature_Returns404()
    {
        var (_, _, signedUrl, _, _) = await CreateSentImageAsync();
        var tampered = signedUrl[..^1] + (signedUrl[^1] == 'A' ? 'B' : 'A');

        using var response = await GetRawAsync(tampered);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_CorrectlySignedButExpiredUrl_Returns404()
    {
        var (_, _, _, fileId, _) = await CreateSentImageAsync();
        var expired = Fixture.Services.GetRequiredService<IFileUrlSigner>().Sign(fileId, DateTime.UtcNow.AddMinutes(-5));

        using var response = await GetRawAsync(expired);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_ConversationMemberWithATokenAndNoSignature_Returns200()
    {
        var (_, bob, _, fileId, _) = await CreateSentImageAsync();

        var response = await Api.GetAsync($"/api/files/{fileId}", bob.Token);

        response.Status.ShouldBe(200);
    }

    [Fact]
    public async Task Download_NonMemberWithATokenOrAnonymousWithoutASignature_Returns404()
    {
        var (_, _, _, fileId, _) = await CreateSentImageAsync();
        var carol = await RegisterAsync("Carol");

        var nonMember = await Api.GetAsync($"/api/files/{fileId}", carol.Token);
        var anonymous = await Api.GetAsync($"/api/files/{fileId}");

        nonMember.Status.ShouldBe(404);
        anonymous.Status.ShouldBe(404);
    }

    [Fact]
    public async Task SendMessage_SomeoneElsesFile_Returns403NotOwned()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var bobsFile = await UploadPngAsync(bob, fileName: "b.png");

        var response = await SendImageAsync(alice, directId, bobsFile.G("id"));

        response.Status.ShouldBe(403);
        response.Code.ShouldBe("File.NotOwned");
    }

    [Fact]
    public async Task SendMessage_AlreadyAttachedFile_Returns400AlreadyAttached()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var file = await UploadPngAsync(alice);
        (await SendImageAsync(alice, directId, file.G("id"))).Status.ShouldBe(201);

        var response = await SendImageAsync(alice, directId, file.G("id"));

        response.Status.ShouldBe(400);
        response.Code.ShouldBe("File.AlreadyAttached");
    }

    [Fact]
    public async Task SendMessage_AvatarFileAsAttachment_Returns400WrongPurpose()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var avatar = await UploadPngAsync(alice, "Avatar", "avatar.png");

        var response = await SendImageAsync(alice, directId, avatar.G("id"));

        response.Status.ShouldBe(400);
        response.Code.ShouldBe("File.WrongPurpose");
    }

    [Fact]
    public async Task Upload_PdfBytesDeclaredAsPng_Returns400ContentMismatch()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(TestFiles.Pdf, "fake.png", "image/png", alice.Token);

        response.Status.ShouldBe(400);
        response.Json.P("errors").P("File")[0].GetString().ShouldBe("File content does not match its type");
    }

    [Fact]
    public async Task Upload_NonImageAvatar_Returns400()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(TestFiles.Pdf, "a.pdf", "application/pdf", alice.Token, "Avatar");

        response.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Upload_UnknownPurpose_Returns400()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(TestFiles.Png, "x.png", "image/png", alice.Token, "Nonsense");

        response.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Upload_Avatar_Returns201WithThePublicUrl()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(TestFiles.Png, "avatar.png", "image/png", alice.Token, "Avatar");

        response.Status.ShouldBe(201);
        response.Json.S("url").ShouldBe($"/api/files/{response.Json.G("id")}");
    }

    [Fact]
    public async Task UpdateMe_AvatarFileId_SetsTheProfileAvatarUrl()
    {
        var alice = await RegisterAsync("Alice");
        var avatarId = (await UploadPngAsync(alice, "Avatar", "avatar.png")).G("id");

        var response = await Api.PutAsync("/api/users/me", new { displayName = "Alice A.", bio = "hi", avatarFileId = avatarId }, alice.Token);

        response.Status.ShouldBe(204);
        (await Api.GetAsync("/api/users/me", alice.Token)).Json.S("avatarUrl").ShouldBe($"/api/files/{avatarId}");
    }

    [Fact]
    public async Task Download_AvatarAnonymously_ReturnsItPubliclyCachedForADay()
    {
        var alice = await RegisterAsync("Alice");
        var avatarId = (await UploadPngAsync(alice, "Avatar", "avatar.png")).G("id");

        using var response = await GetRawAsync($"/api/files/{avatarId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("image/png");
        var cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
        cacheControl.Public.ShouldBeTrue();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(1));
        response.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateMe_ReplacingTheAvatar_MarksTheOldFileDeletedAndItsUrlReturns404()
    {
        var alice = await RegisterAsync("Alice");
        var firstId = (await UploadPngAsync(alice, "Avatar", "avatar.png")).G("id");
        (await Api.PutAsync("/api/users/me", new { displayName = "Alice", avatarFileId = firstId }, alice.Token)).Status.ShouldBe(204);
        var secondId = (await UploadPngAsync(alice, "Avatar", "avatar2.png")).G("id");

        var response = await Api.PutAsync("/api/users/me", new { displayName = "Alice", avatarFileId = secondId }, alice.Token);

        response.Status.ShouldBe(204);
        (await IsMarkedDeletedAsync(firstId)).ShouldBeTrue();
        (await Api.Http.GetAsync($"/api/files/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Api.Http.GetAsync($"/api/files/{secondId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_Pdf_ReturnsAnAttachmentWithTheFileNameAndNoSniff()
    {
        var alice = await RegisterAsync("Alice");
        var upload = await Api.UploadAsync(TestFiles.Pdf, "report.pdf", "application/pdf", alice.Token);

        using var response = await GetRawAsync($"/api/files/{upload.Json.G("id")}", alice.Token);

        upload.Status.ShouldBe(201);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/pdf");
        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileName.ShouldNotBeNull().Trim('"').ShouldBe("report.pdf");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task Upload_Html_Returns400()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "x.html", "text/html", alice.Token);

        response.Status.ShouldBe(400);
        response.HasError("File").ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_WithoutAFilePart_Returns400()
    {
        var alice = await RegisterAsync("Alice");
        using var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files?purpose=MessageAttachment") { Content = form };
        request.Headers.Authorization = new("Bearer", alice.Token);

        var response = await Api.SendAsync(request);

        response.Status.ShouldBe(400);
        response.HasError("file").ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_OneByteOverTheMaximumSize_Returns400()
    {
        var alice = await RegisterAsync("Alice");
        var bigPng = new byte[20 * 1024 * 1024 + 1];
        TestFiles.Png.AsSpan(0, 8).CopyTo(bigPng);

        var response = await Api.UploadAsync(bigPng, "big.png", "image/png", alice.Token);

        response.Status.ShouldBe(400);
        response.HasError("File").ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_OverTheRequestLimit_ReturnsAProblem()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.UploadAsync(new byte[25 * 1024 * 1024], "huge.png", "image/png", alice.Token);

        response.Status.ShouldBeOneOf(413, 400);
        response.IsProblem.ShouldBeTrue();
    }

    [Fact]
    public async Task Upload_WithoutAToken_Returns401()
    {
        var response = await Api.UploadAsync(TestFiles.Png, "pixel.png", "image/png", null);

        response.Status.ShouldBe(401);
    }

    [Fact]
    public async Task DeleteMessage_WithAnAttachment_MarksTheFileDeletedAndItsUrlsReturn404()
    {
        var (alice, bob, signedUrl, fileId, messageId) = await CreateSentImageAsync();

        var response = await Api.DeleteAsync($"/api/messages/{messageId}", alice.Token);

        response.Status.ShouldBe(204);
        (await IsMarkedDeletedAsync(fileId)).ShouldBeTrue();
        (await Api.Http.GetAsync(signedUrl)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Api.GetAsync($"/api/files/{fileId}", bob.Token)).Status.ShouldBe(404);
    }

    private Task<ApiResponse> SendImageAsync(TestUser sender, Guid conversationId, Guid fileId) =>
        Api.PostAsync($"/api/conversations/{conversationId}/messages", new { type = "Image", attachmentFileId = fileId }, sender.Token);

    private Task<string> StorageKeyOfAsync(Guid fileId) =>
        QueryDbAsync(db => db.UploadedFiles.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.StorageKey).FirstAsync());

    private Task<bool> IsMarkedDeletedAsync(Guid fileId) =>
        QueryDbAsync(db => db.UploadedFiles.AsNoTracking().AnyAsync(f => f.Id == fileId && f.IsDeleted));

    private async Task<string> StorageFolderOfAsync(TestUser owner)
    {
        var key = await StorageKeyOfAsync((await UploadPngAsync(owner)).G("id"));
        return Path.GetDirectoryName(Path.Combine(Fixture.FilesRoot, key))!;
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId)> CreateChatAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        return (alice, bob, await CreateDirectAsync(alice, bob.Id));
    }

    private async Task<(TestUser Alice, TestUser Bob, string SignedUrl, Guid FileId, Guid MessageId)> CreateSentImageAsync()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var file = await UploadPngAsync(alice);
        var message = await SendImageAsync(alice, directId, file.G("id"));
        message.Status.ShouldBe(201);
        return (alice, bob, message.Json.P("attachment").S("url")!, file.G("id"), message.Json.G("id"));
    }
}
