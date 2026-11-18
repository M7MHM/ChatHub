using System.Collections.Concurrent;
using System.Text.Json;
using ChatHub.Application.Common.RealTime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed record HubEvent(string Name, JsonElement Payload);

public sealed class HubClient : IAsyncDisposable
{
    private static readonly string[] EventNames =
    [
        ChatEvents.ConversationCreated, ChatEvents.MessageReceived, ChatEvents.MessageUpdated, ChatEvents.MessageDeleted,
        ChatEvents.MessagesRead, ChatEvents.MessagesDelivered, ChatEvents.UserTyping, ChatEvents.PresenceChanged,
        ChatEvents.MemberAdded, ChatEvents.MemberRemoved, ChatEvents.MemberRoleChanged, ChatEvents.ConversationUpdated,
        ChatEvents.ProfileUpdated
    ];

    private readonly ConcurrentQueue<HubEvent> _events = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HubClient(TestServer server, string? accessToken)
    {
        var path = accessToken is null ? "hubs/chat" : $"hubs/chat?access_token={Uri.EscapeDataString(accessToken)}";
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .Build();

        foreach (var name in EventNames)
            Connection.On<JsonElement>(name, payload => _events.Enqueue(new HubEvent(name, payload)));

        Connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public HubConnectionState State => Connection.State;

    public IReadOnlyList<HubEvent> Events => [.. _events];

    public Task StartAsync() => Connection.StartAsync();

    public async Task<bool> WaitForCloseAsync(TimeSpan timeout) =>
        await Task.WhenAny(_closed.Task, Task.Delay(timeout)) == _closed.Task;

    public async Task<JsonElement> WaitForAsync(string name, Func<JsonElement, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            foreach (var hubEvent in _events)
                if (hubEvent.Name == name && match(hubEvent.Payload))
                    return hubEvent.Payload;
            await Task.Delay(20);
        }

        throw new ShouldAssertException(
            $"No matching {name} event arrived. Received: [{string.Join(", ", _events.Select(e => e.Name))}]");
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
