using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

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

    private IUserConnectionTracker Tracker => Fixture.Services.GetRequiredService<IUserConnectionTracker>();

    protected ApiClient NewClient(string? address = null) => NewClient(Fixture.Factory, address);

    protected static ApiClient NewClient(ChatHubApiFactory factory, string? address = null) =>
        new(factory.CreateDefaultClient(new ClientAddressHandler(address ?? ClientAddressHandler.NextAddress())));

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

    protected Task<HubClient> ConnectHubAsync(Guid userId, string accessToken) =>
        ConnectHubAsync(Fixture.Server, userId, accessToken);

    protected async Task<HubClient> ConnectHubAsync(TestServer server, Guid userId, string accessToken, bool skipNegotiation = false)
    {
        var existing = (await Tracker.GetConnectionIdsAsync(userId)).ToHashSet();
        var hub = Track(new HubClient(server, accessToken, skipNegotiation), userId);
        await hub.StartAsync();
        await WaitUntilConnectedOnServerAsync(userId, existing);
        return hub;
    }

    protected HubClient CreateHub(string? accessToken)
    {
        var userId = accessToken is null ? (Guid?)null : Guid.Parse(new JsonWebToken(accessToken).Subject);
        return Track(new HubClient(Fixture.Server, accessToken), userId);
    }

    protected string ForgeToken(IDictionary<string, object> claims, TimeSpan lifetime)
    {
        var settings = Fixture.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Expires = DateTime.UtcNow + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret)), SecurityAlgorithms.HmacSha256)
        });
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

    private async Task WaitUntilConnectedOnServerAsync(Guid userId, IReadOnlySet<string> existingConnections)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               !((await Tracker.GetConnectionIdsAsync(userId)).Any(id => !existingConnections.Contains(id)) &&
                 await QueryDbAsync(db => db.UserProfiles.AnyAsync(u => u.Id == userId && u.IsOnline))))
            await Task.Delay(20);
    }

    private async Task WaitUntilHubUsersAreOfflineAsync()
    {
        if (_hubUsers.Count == 0) return;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && await AnyHubUserTrackedAsync())
            await Task.Delay(20);

        var users = _hubUsers.ToList();
        while (DateTime.UtcNow < deadline &&
               await QueryDbAsync(db => db.UserProfiles.AnyAsync(u => users.Contains(u.Id) && u.IsOnline)))
            await Task.Delay(20);
    }

    private async Task<bool> AnyHubUserTrackedAsync()
    {
        foreach (var userId in _hubUsers)
            if (await Tracker.IsOnlineAsync(userId))
                return true;
        return false;
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
