using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class FileCleanupTests(DatabaseFixture database) : AppTestBase(database)
{
    private FileCleanupService Cleanup => Services.GetServices<IHostedService>().OfType<FileCleanupService>().Single();

    [Fact]
    public async Task RunOnce_DeletedFile_RemovesItFromDiskAndDatabase()
    {
        var (yara, _, direct) = await CreateUsersAsync();
        var file = await UploadPngAsync(yara);
        var message = await SendFileAsync(yara, direct, MessageType.Image, file.Id);
        var key = await StorageKeyOfAsync(file.Id);
        (await SendAsync(yara, new DeleteMessageCommand(message.Id))).ShouldSucceed();

        var result = await Cleanup.RunOnceAsync(CancellationToken.None);

        result.ShouldBe(new FileCleanupResult(Orphaned: 0, Deleted: 1));
        File.Exists(Path.Combine(FilesRoot, key)).ShouldBeFalse();
        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == file.Id))).ShouldBeFalse();
    }

    [Fact]
    public async Task RunOnce_FreshUnattachedUpload_KeepsIt()
    {
        var (yara, _, _) = await CreateUsersAsync();
        var orphan = await UploadPngAsync(yara, fileName: "orphan.png");
        var key = await StorageKeyOfAsync(orphan.Id);

        await Cleanup.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(FilesRoot, key)).ShouldBeTrue();
        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == orphan.Id))).ShouldBeTrue();
    }

    [Fact]
    public async Task RunOnce_UnattachedUploadOlderThanTheOrphanAge_RemovesItFromDiskAndDatabase()
    {
        var (yara, _, _) = await CreateUsersAsync();
        var orphan = await UploadPngAsync(yara, fileName: "orphan.png");
        var key = await StorageKeyOfAsync(orphan.Id);

        Clock.Advance(FileCleanupService.OrphanAge + TimeSpan.FromHours(1));
        var result = await Cleanup.RunOnceAsync(CancellationToken.None);

        result.ShouldBe(new FileCleanupResult(Orphaned: 1, Deleted: 0));
        File.Exists(Path.Combine(FilesRoot, key)).ShouldBeFalse();
        (await QueryDbAsync(db => db.UploadedFiles.AnyAsync(f => f.Id == orphan.Id))).ShouldBeFalse();
    }

    [Fact]
    public async Task RunOnce_AttachedFilesOlderThanTheOrphanAge_KeepsThem()
    {
        var (yara, zane, direct) = await CreateUsersAsync();
        var voice = await UploadAsync(yara, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");
        await SendFileAsync(yara, direct, MessageType.Voice, voice.Id);
        var groupImage = await UploadPngAsync(yara, FilePurpose.GroupImage, "g.png");
        (await SendAsync(yara, new CreateGroupCommand("Files", groupImage.Id, [zane]))).ShouldSucceed();

        Clock.Advance(FileCleanupService.OrphanAge + TimeSpan.FromHours(1));
        var result = await Cleanup.RunOnceAsync(CancellationToken.None);

        result.ShouldBe(new FileCleanupResult(Orphaned: 0, Deleted: 0));
        (await QueryDbAsync(db => db.UploadedFiles.CountAsync(f => f.Id == voice.Id || f.Id == groupImage.Id))).ShouldBe(2);
    }

    [Fact]
    public async Task RunOnce_TwoInstancesAtTheSameTime_RemoveEachFileExactlyOnceWithoutErrors()
    {
        var (yara, _, _) = await CreateUsersAsync();
        var keys = new List<string>();
        for (var i = 0; i < 20; i++)
            keys.Add(await StorageKeyOfAsync((await UploadPngAsync(yara, fileName: $"orphan-{i}.png")).Id));
        var otherInstance = new FileCleanupService(Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<IOptions<FileStorageSettings>>(), Clock, NullLogger<FileCleanupService>.Instance);

        Clock.Advance(FileCleanupService.OrphanAge + TimeSpan.FromHours(1));
        var results = await Task.WhenAll(
            Task.Run(() => Cleanup.RunOnceAsync(CancellationToken.None)),
            Task.Run(() => otherInstance.RunOnceAsync(CancellationToken.None)));

        results.Sum(r => r.Orphaned).ShouldBe(20);
        results.Sum(r => r.Deleted).ShouldBe(0);
        keys.ShouldAllBe(key => !File.Exists(Path.Combine(FilesRoot, key)));
        (await QueryDbAsync(db => db.UploadedFiles.CountAsync())).ShouldBe(0);
    }

    private Task<string> StorageKeyOfAsync(Guid fileId) =>
        QueryDbAsync(db => db.UploadedFiles.Where(f => f.Id == fileId).Select(f => f.StorageKey).FirstAsync());

    private async Task<(Guid Yara, Guid Zane, Guid Direct)> CreateUsersAsync()
    {
        var yara = await CreateUserAsync("Yara");
        var zane = await CreateUserAsync("Zane");
        return (yara, zane, await CreateDirectAsync(yara, zane));
    }
}
