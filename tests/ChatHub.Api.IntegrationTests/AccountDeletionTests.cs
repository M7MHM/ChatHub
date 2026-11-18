using System.Net;
using ChatHub.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatHub.Api.IntegrationTests;

public sealed class AccountDeletionTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task BeforeDeletion_User_CanMessageAndConnectToTheHub()
    {
        var (alice, carol) = (await RegisterAsync("Alice"), await RegisterAsync("Carol"));
        var direct = await Api.PostAsync("/api/conversations/direct", new { otherUserId = alice.Id }, carol.Token);

        var sent = await Api.PostAsync($"/api/conversations/{direct.Json.G("conversationId")}/messages",
            new { type = "Text", content = "bye" }, carol.Token);
        var hub = await ConnectHubAsync(carol);

        direct.Status.ShouldBe(201);
        sent.Status.ShouldBe(201);
        hub.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task DeleteMe_Authenticated_Returns204()
    {
        var carol = await RegisterAsync("Carol");

        var response = await Api.DeleteAsync("/api/users/me", carol.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task DeleteMe_StillValidAccessToken_Returns401Problem()
    {
        var carol = await RegisterAsync("Carol");
        (await Api.DeleteAsync("/api/users/me", carol.Token)).Status.ShouldBe(204);

        var response = await Api.GetAsync("/api/users/me", carol.Token);

        response.Status.ShouldBe(401);
        response.IsProblem.ShouldBeTrue();
    }

    [Theory]
    [InlineData("send message")]
    [InlineData("list conversations")]
    public async Task DeleteMe_StillValidAccessToken_Returns401OnAnyEndpoint(string endpoint)
    {
        var alice = await RegisterAsync("Alice");
        var carol = await RegisterAsync("Carol");
        var directId = await CreateDirectAsync(carol, alice.Id);
        (await Api.DeleteAsync("/api/users/me", carol.Token)).Status.ShouldBe(204);

        var response = endpoint == "send message"
            ? await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "Text", content = "ghost" }, carol.Token)
            : await Api.GetAsync("/api/conversations", carol.Token);

        response.Status.ShouldBe(401);
    }

    [Fact]
    public async Task DeleteMe_StillValidAccessToken_HubConnectionIsRejectedWith401()
    {
        var carol = await RegisterAsync("Carol");
        (await Api.DeleteAsync("/api/users/me", carol.Token)).Status.ShouldBe(204);
        var hub = CreateHub(carol.Token);

        var exception = await Should.ThrowAsync<HttpRequestException>(hub.StartAsync);

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
