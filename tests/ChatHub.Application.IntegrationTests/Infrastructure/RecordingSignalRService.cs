using ChatHub.Application.Common.Interfaces;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public sealed record Delivery(Guid UserId, string Method, object Payload, string Route)
{
    public const string ToUser = "user";
    public const string ToUsers = "users";
    public const string ToGroup = "group";
}

public sealed record UsersCall(IReadOnlyList<Guid> UserIds, string Method);

public sealed class RecordingSignalRService : ISignalRService
{
    private readonly Lock _lock = new();
    private readonly List<Delivery> _deliveries = [];
    private readonly List<string> _groupCalls = [];
    private readonly List<UsersCall> _usersCalls = [];
    private readonly List<Guid> _disconnects = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _groups = [];

    public string? ThrowOn { get; set; }

    public IReadOnlyList<string> GroupCalls
    {
        get { lock (_lock) return [.. _groupCalls]; }
    }

    public IReadOnlyList<UsersCall> UsersCalls
    {
        get { lock (_lock) return [.. _usersCalls]; }
    }

    public IReadOnlyList<Guid> Disconnects
    {
        get { lock (_lock) return [.. _disconnects]; }
    }

    public int Mark()
    {
        lock (_lock) return _deliveries.Count;
    }

    public IReadOnlyList<Delivery> Since(int mark)
    {
        lock (_lock) return _deliveries.Skip(mark).ToList();
    }

    public int MarkUsersCalls()
    {
        lock (_lock) return _usersCalls.Count;
    }

    public IReadOnlyList<UsersCall> UsersCallsSince(int mark)
    {
        lock (_lock) return _usersCalls.Skip(mark).ToList();
    }

    public static string Added(Guid userId, Guid conversationId) => $"add {userId} {conversationId}";

    public static string Removed(Guid userId, Guid conversationId) => $"remove {userId} {conversationId}";

    public Task SendToUserAsync(Guid userId, string method, object payload, CancellationToken cancellationToken = default)
    {
        FailIfRequested(method);
        lock (_lock) _deliveries.Add(new Delivery(userId, method, payload, Delivery.ToUser));
        return Task.CompletedTask;
    }

    public Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload, CancellationToken cancellationToken = default)
    {
        FailIfRequested(method);
        var ids = userIds.ToList();
        lock (_lock)
        {
            _usersCalls.Add(new UsersCall(ids, method));
            foreach (var id in ids) _deliveries.Add(new Delivery(id, method, payload, Delivery.ToUsers));
        }
        return Task.CompletedTask;
    }

    public Task SendToConversationAsync(Guid conversationId, string method, object payload, CancellationToken cancellationToken = default)
    {
        FailIfRequested(method);
        lock (_lock)
        {
            if (_groups.TryGetValue(conversationId, out var members))
                foreach (var userId in members) _deliveries.Add(new Delivery(userId, method, payload, Delivery.ToGroup));
        }
        return Task.CompletedTask;
    }

    public Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _groupCalls.Add(Added(userId, conversationId));
            if (!_groups.TryGetValue(conversationId, out var members)) _groups[conversationId] = members = [];
            members.Add(userId);
        }
        return Task.CompletedTask;
    }

    public Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _groupCalls.Add(Removed(userId, conversationId));
            if (_groups.TryGetValue(conversationId, out var members)) members.Remove(userId);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        lock (_lock) _disconnects.Add(userId);
        return Task.CompletedTask;
    }

    private void FailIfRequested(string method)
    {
        if (method == ThrowOn) throw new InvalidOperationException($"SignalR down ({method})");
    }
}

public static class DeliveryAssertions
{
    public static object? OnlyPayload(this IReadOnlyList<Delivery> deliveries, Guid userId, string method)
    {
        var hits = deliveries.Where(d => d.UserId == userId && d.Method == method).ToList();
        hits.Count.ShouldBe(1, $"expected exactly one {method} for {userId}");
        return hits[0].Payload;
    }

    public static void ShouldReachGroupOnce(this IReadOnlyList<Delivery> deliveries, string method, params Guid[] userIds)
    {
        var hits = deliveries.Where(d => d.Method == method).ToList();
        hits.ShouldAllBe(d => d.Route == Delivery.ToGroup);
        hits.Select(d => d.UserId).ShouldBe(userIds, ignoreOrder: true);
    }
}
