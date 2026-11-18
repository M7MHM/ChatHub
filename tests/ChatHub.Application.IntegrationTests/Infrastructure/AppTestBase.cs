using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Files.Commands;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Domain.Common;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

[Collection(DatabaseCollection.Name)]
public abstract partial class AppTestBase(DatabaseFixture database) : IAsyncLifetime
{
    protected const string Password = "secret123";

    private static readonly Lazy<string> PasswordHash = new(() => new PasswordHasher().Hash(Password));
    private ServiceProvider? _services;

    protected DatabaseFixture Database { get; } = database;
    protected TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    protected RecordingSignalRService SignalR { get; } = new();
    protected RecordingPasswordHasher Hasher { get; } = new();
    protected LogCapture Logs { get; } = new();
    protected DomainEventRecorder DomainEvents { get; } = new();
    protected string FilesRoot { get; } = database.CreateFilesRoot();

    protected IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The test host is not initialized.");

    public async Task InitializeAsync()
    {
        await Database.ResetAsync();
        _services = BuildServices();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        TestDirectories.TryDelete(FilesRoot);
        Logs.Errors.ShouldBeEmpty("ChatHub code logged unexpected errors during the test");
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = Database.ConnectionString,
                ["Jwt:Secret"] = "application-tests-jwt-secret-0123456789abcdef",
                ["Jwt:Issuer"] = "ChatHub.Tests",
                ["Jwt:Audience"] = "ChatHub.Tests.Clients",
                ["FileStorage:RootPath"] = FilesRoot,
                ["FileStorage:SigningKey"] = "application-tests-file-signing-key-0123456789"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(FilesRoot));
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter(LogCapture.CommandCategory, LogLevel.Information)
            .AddProvider(Logs));
        services.AddSingleton<TimeProvider>(Clock);

        services.AddApplication();
        services.AddInfrastructure(configuration);

        services.RemoveAll<ICurrentUserService>();
        services.AddScoped<TestCurrentUser>();
        services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<TestCurrentUser>());
        services.RemoveAll<ISignalRService>();
        services.AddSingleton<ISignalRService>(SignalR);
        services.RemoveAll<IPasswordHasher>();
        services.AddSingleton<IPasswordHasher>(Hasher);

        services.AddSingleton(DomainEvents);
        foreach (var eventType in typeof(IDomainEvent).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IDomainEvent))))
        {
            services.AddTransient(
                typeof(INotificationHandler<>).MakeGenericType(typeof(DomainEventNotification<>).MakeGenericType(eventType)),
                typeof(RecordingDomainEventHandler<>).MakeGenericType(eventType));
        }

        ConfigureServices(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    protected async Task<T> SendAsync<T>(Guid? userId, IRequest<T> request)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestCurrentUser>().UserId = userId;
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    protected async Task<T> QueryDbAsync<T>(Func<ApplicationDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    protected async Task UseDbAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    protected static Task PauseAsync() => Task.Delay(5);

    protected async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var barrier = new Barrier(2);

        Task<T> Run(Func<Task<T>> action) => Task.Run(async () =>
        {
            Clock.OnCall(call =>
            {
                if (call == 1 && !barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The other racer never reached the barrier.");
            });
            return await action();
        });

        var a = Run(first);
        var b = Run(second);
        await Task.WhenAll(a, b);
        return (a.Result, b.Result);
    }

    protected async Task<Guid> CreateUserAsync(string displayName, string? email = null, string? phone = null)
    {
        email ??= phone is null ? $"{displayName.ToLowerInvariant().Replace(' ', '.')}@example.com" : null;
        var user = UserProfile.Create(displayName, PasswordHash.Value,
            phone is null ? null : PhoneNumber.Create(phone),
            email is null ? null : Email.Create(email));

        await UseDbAsync(async db =>
        {
            db.UserProfiles.Add(user);
            await db.SaveChangesAsync();
        });
        return user.Id;
    }

    protected async Task<AuthResponse> RegisterAsync(string displayName, string? email = null, string? phone = null) =>
        (await SendAsync(null, new RegisterCommand(displayName, email, phone, Password))).ShouldSucceed();

    protected async Task<AuthResponse> LoginAsync(string identifier, string password = Password) =>
        (await SendAsync(null, new LoginCommand(identifier, password))).ShouldSucceed();

    protected Task<Result<AuthResponse>> RefreshAsync(string refreshToken) =>
        SendAsync(null, new RefreshCommand(refreshToken));

    protected Task<Guid> StampOfAsync(Guid userId) =>
        QueryDbAsync(db => db.UserProfiles.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).FirstAsync());

    protected async Task GoOnlineAsync(Guid userId) =>
        await UseDbAsync(async db =>
        {
            var user = await db.UserProfiles.FirstAsync(u => u.Id == userId);
            user.GoOnline();
            await db.SaveChangesAsync();
        });

    protected async Task BlockAsync(Guid blockerId, Guid blockedId) =>
        (await SendAsync(blockerId, new BlockUserCommand(blockedId))).ShouldSucceed();

    protected async Task<Guid> CreateDirectAsync(Guid userId, Guid otherUserId) =>
        (await SendAsync(userId, new CreateDirectConversationCommand(otherUserId))).ShouldSucceed().ConversationId;

    protected async Task<Guid> CreateGroupAsync(Guid ownerId, string title, params Guid[] memberIds) =>
        (await SendAsync(ownerId, new CreateGroupCommand(title, null, memberIds))).ShouldSucceed().ConversationId;

    protected async Task AddMemberAsync(Guid actorId, Guid conversationId, Guid userId) =>
        (await SendAsync(actorId, new AddMemberCommand(userId) { ConversationId = conversationId })).ShouldSucceed();

    protected async Task ClearChatAsync(Guid userId, Guid conversationId) =>
        (await SendAsync(userId, new ClearChatCommand(conversationId))).ShouldSucceed();

    protected Task<Result<MessageResponse>> TrySendAsync(Guid senderId, Guid conversationId, MessageType type,
        string? content, Guid? attachmentFileId = null, Guid? replyToId = null) =>
        SendAsync(senderId, new SendMessageCommand(type, content, attachmentFileId, replyToId) { ConversationId = conversationId });

    protected async Task<MessageResponse> SendTextAsync(Guid senderId, Guid conversationId, string text, Guid? replyToId = null) =>
        (await TrySendAsync(senderId, conversationId, MessageType.Text, text, replyToId: replyToId)).ShouldSucceed();

    protected async Task<MessageResponse> SendFileAsync(Guid senderId, Guid conversationId, MessageType type, Guid fileId,
        string? caption = null) =>
        (await TrySendAsync(senderId, conversationId, type, caption, fileId)).ShouldSucceed();

    protected async Task<Guid> AddMessageViaDomainAsync(Guid conversationId, Guid senderId, MessageType type, string? content,
        Attachment? attachment = null)
    {
        var id = Guid.Empty;
        await UseDbAsync(async db =>
        {
            var conversation = await db.Conversations.Include(c => c.Members).FirstAsync(c => c.Id == conversationId);
            var message = conversation.SendMessage(senderId, type, content, attachment);
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            id = message.Id;
        });
        return id;
    }

    protected Task<Result<FileResponse>> TryUploadAsync(Guid userId, FilePurpose purpose, byte[] bytes, string fileName,
        string contentType) =>
        SendAsync(userId, new UploadFileCommand(purpose, new MemoryStream(bytes), fileName, contentType, bytes.Length));

    protected async Task<FileResponse> UploadAsync(Guid userId, FilePurpose purpose, byte[] bytes, string fileName,
        string contentType) =>
        (await TryUploadAsync(userId, purpose, bytes, fileName, contentType)).ShouldSucceed();

    protected Task<FileResponse> UploadPngAsync(Guid userId, FilePurpose purpose = FilePurpose.MessageAttachment,
        string fileName = "image.png") =>
        UploadAsync(userId, purpose, TestFiles.Png, fileName, "image/png");
}
