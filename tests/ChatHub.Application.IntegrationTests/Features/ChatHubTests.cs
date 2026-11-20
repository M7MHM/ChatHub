using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Infrastructure.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using ChatHubEndpoint = ChatHub.Infrastructure.RealTime.Hubs.ChatHub;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class ChatHubTests(DatabaseFixture database) : AppTestBase(database)
{
    private readonly UserConnectionTracker _tracker = new();

    [Fact]
    public async Task OnConnected_ActiveUser_IsTrackedAndJoinsEachConversationGroup()
    {
        var vera = await CreateUserAsync("Vera");
        var walt = await CreateUserAsync("Walt");
        await CreateDirectAsync(vera, walt);
        await CreateGroupAsync(vera, "Security", walt);
        var groups = new FakeGroupManager();

        var connection = await ConnectAsync(vera, groups);

        connection.Aborted.ShouldBeFalse();
        _tracker.IsOnline(vera).ShouldBeTrue();
        groups.Joined.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OnConnected_DeletedUser_IsAbortedNotTrackedAndJoinsNothing()
    {
        var vera = await CreateUserAsync("Vera");
        var xena = await CreateUserAsync("Xena");
        await CreateGroupAsync(vera, "Security", xena);
        (await SendAsync(xena, new DeleteMyAccountCommand())).ShouldSucceed();
        var groups = new FakeGroupManager();

        var connection = await ConnectAsync(xena, groups);

        connection.Aborted.ShouldBeTrue();
        _tracker.IsOnline(xena).ShouldBeFalse();
        groups.Joined.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisconnectUser_TrackedConnections_AbortsEveryConnectionOfThatUserOnly()
    {
        var yara = Guid.NewGuid();
        var zane = Guid.NewGuid();
        FakeHubCallerContext yaraTab1 = new(yara), yaraTab2 = new(yara), zaneTab = new(zane);
        _tracker.Add(yara, yaraTab1);
        _tracker.Add(yara, yaraTab2);
        _tracker.Add(zane, zaneTab);
        var service = new SignalRService(Services.GetRequiredService<IHubContext<ChatHubEndpoint>>(), _tracker);

        await service.DisconnectUserAsync(yara);

        yaraTab1.Aborted.ShouldBeTrue();
        yaraTab2.Aborted.ShouldBeTrue();
        zaneTab.Aborted.ShouldBeFalse();
    }

    private async Task<FakeHubCallerContext> ConnectAsync(Guid userId, FakeGroupManager groups)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = new FakeHubCallerContext(userId);
        using var hub = new ChatHubEndpoint(_tracker, scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<HybridCache>(), scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>())
        {
            Context = context,
            Groups = groups
        };
        await hub.OnConnectedAsync();
        return context;
    }
}
