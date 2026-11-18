using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public sealed class FakeHubCallerContext(Guid userId) : HubCallerContext
{
    public bool Aborted { get; private set; }

    public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    public override string? UserIdentifier => userId.ToString();

    public override ClaimsPrincipal? User { get; } =
        new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort() => Aborted = true;
}

public sealed class FakeGroupManager : IGroupManager
{
    private readonly List<string> _joined = [];

    public IReadOnlyList<string> Joined => _joined;

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        _joined.Add(groupName);
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
