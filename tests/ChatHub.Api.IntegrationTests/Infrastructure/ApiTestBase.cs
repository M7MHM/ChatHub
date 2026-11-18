using System.Net.Http.Headers;
using System.Text.Json;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(ApiFixture fixture) : IAsyncLifetime
{
    protected const string Password = "secret123";

    private readonly List<HubClient> _hubs = [];
    private readonly HashSet<Guid> _hubUsers = [];
    private int _logMark;

    protected ApiFixture Fixture { get; } = fixture;
    protected ApiClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        Api = NewClient();
        _logMark = Fixture.Logs.Mark();
    }

    public async Task DisposeAsync()
    {
        foreach (var hub in _hubs) await hub.DisposeAsync();
        await WaitUntilHubUsersAreOfflineAsync();
        Fixture.Logs.Since(_logMark).ShouldBeEmpty("the API logged unexpected errors during the test");
    }

    protected ApiClient NewClient(string? address = null) =>
        new(Fixture.Factory.CreateDefaultClient(new ClientAddressHandler(address ?? ClientAddressHandler.NextAddress())));

    protected IReadOnlyList<LogEntry> TakeLoggedErrors()
    {
        var errors = Fixture.Logs.Since(_logMark);
        _logMark += errors.Count;
        return errors;
    }

    protected async Task<TestUser> RegisterAsync(string displayName, string? email = null, string? phone = null)
    {
        email ??= phone is null ? $"{displayName.ToLowerInvariant()}@example.com" : null;
        var response = await NewClient().PostAsync("/api/auth/register",
            new { displayName, email, phoneNumber = phone, password = Password });
        response.Status.ShouldBe(201, response.Text);
        return TestUser.From(response.Json, email);
    }

    protected async Task<TestUser> LoginAsync(string identifier, string password = Password)
    {
        var response = await NewClient().PostAsync("/api/auth/login", new { identifier, password });
        response.Status.ShouldBe(200, response.Text);
        return TestUser.From(response.Json, identifier);
    }

    protected async Task<Guid> CreateDirectAsync(TestUser user, Guid otherUserId)
    {
        var response = await Api.PostAsync("/api/conversations/direct", new { otherUserId }, user.Token);
        response.Status.ShouldBeOneOf(200, 201);
        return response.Json.G("conversationId");
    }

    protected async Task<Guid> CreateGroupAsync(TestUser owner, string title, params Guid[] memberIds)
    {
        var response = await Api.PostAsync("/api/conversations/groups", new { title, memberIds }, owner.Token);
        response.Status.ShouldBe(201, response.Text);
        return response.Json.G("conversationId");
    }

    protected async Task<JsonElement> SendTextAsync(TestUser sender, Guid conversationId, string content)
    {
        var response = await Api.PostAsync($"/api/conversations/{conversationId}/messages", new { type = "Text", content }, sender.Token);
        response.Status.ShouldBe(201, response.Text);
        return response.Json;
    }

    protected async Task<JsonElement> UploadPngAsync(TestUser owner, string purpose = "MessageAttachment", string fileName = "pixel.png")
    {
        var response = await Api.UploadAsync(TestFiles.Png, fileName, "image/png", owner.Token, purpose);
        response.Status.ShouldBe(201, response.Text);
        return response.Json;
    }

    protected async Task<HubClient> ConnectHubAsync(TestUser user) => await ConnectHubAsync(user.Id, user.Token);

    protected async Task<HubClient> ConnectHubAsync(Guid userId, string accessToken)
    {
        var hub = Track(new HubClient(Fixture.Server, accessToken), userId);
        await hub.StartAsync();
        await WaitUntilConnectedOnServerAsync(userId);
        return hub;
    }

    protected HubClient CreateHub(string? accessToken)
    {
        var userId = accessToken is null ? (Guid?)null : Guid.Parse(new JsonWebToken(accessToken).Subject);
        return Track(new HubClient(Fixture.Server, accessToken), userId);
    }

    protected async Task<T> QueryDbAsync<T>(Func<ApplicationDbContext, Task<T>> query)
    {
        await using var scope = Fixture.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    protected Task<HttpResponseMessage> GetRawAsync(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Api.Http.SendAsync(request);
    }

    private HubClient Track(HubClient hub, Guid? userId)
    {
        _hubs.Add(hub);
        if (userId is { } id) _hubUsers.Add(id);
        return hub;
    }

    private async Task WaitUntilConnectedOnServerAsync(Guid userId)
    {
        var tracker = Fixture.Services.GetRequiredService<UserConnectionTracker>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               !(tracker.IsOnline(userId) && await QueryDbAsync(db => db.UserProfiles.AnyAsync(u => u.Id == userId && u.IsOnline))))
            await Task.Delay(20);
    }

    private async Task WaitUntilHubUsersAreOfflineAsync()
    {
        if (_hubUsers.Count == 0) return;

        var tracker = Fixture.Services.GetRequiredService<UserConnectionTracker>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_hubUsers.Any(tracker.IsOnline) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var users = _hubUsers.ToList();
        while (DateTime.UtcNow < deadline &&
               await QueryDbAsync(db => db.UserProfiles.AnyAsync(u => users.Contains(u.Id) && u.IsOnline)))
            await Task.Delay(20);
    }
}

public sealed record TestUser(Guid Id, string Token, string RefreshToken, string? Identifier)
{
    public static TestUser From(JsonElement auth, string? identifier) =>
        new(auth.P("user").G("id"), auth.S("accessToken")!, auth.S("refreshToken")!, identifier);
}

public static class TestFiles
{
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"u8.ToArray();
}
