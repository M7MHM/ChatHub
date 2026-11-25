using System.Text;
using System.Text.Json;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Messaging;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;

namespace ChatHub.Notifications.Tests;

public sealed class ConsumerTests(NotificationsFixture fixture) : NotificationsTestBase(fixture), IAsyncLifetime
{
    private readonly FlakyProcessor.Failures _failures = new();
    private IConnection? _connection;
    private IChannel? _channel;

    async Task IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        _connection = await Fixture.ConnectAsync();
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await Services.GetRequiredService<EventConsumer>().StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(Services.GetRequiredService<EventConsumer>().IsConnected));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Services.GetRequiredService<EventConsumer>().StopAsync(CancellationToken.None);
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        await DisposeAsync();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_failures);
        services.RemoveAll<IIntegrationEventProcessor>();
        services.AddScoped<IntegrationEventProcessor>();
        services.AddScoped<IIntegrationEventProcessor>(sp =>
            new FlakyProcessor(sp.GetRequiredService<IntegrationEventProcessor>(), sp.GetRequiredService<FlakyProcessor.Failures>()));
    }

    [Fact]
    public async Task PublishedEvent_IsConsumedAndAcknowledged()
    {
        var userId = Guid.CreateVersion7();

        await PublishAsync(new UserRegistered(Guid.CreateVersion7(), Now, userId, "nora@example.com", "Nora"));

        await WaitUntilAsync(() => QueryAsync(db => db.Recipients.AnyAsync(r => r.UserId == userId)));
        await WaitUntilAsync(async () => await CountAsync(NotificationTopology.Queue) == 0);
        (await CountAsync(NotificationTopology.DeadLetterQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsProcessedOnce()
    {
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var chat = Guid.CreateVersion7();
        var message = Message(chat, alice, "Alice", [bob]);

        await PublishAsync(message);
        await PublishAsync(message);

        await WaitUntilAsync(() => QueryAsync(db => db.ProcessedMessages.AnyAsync(m => m.MessageId == message.MessageId)));
        await WaitUntilAsync(async () => await CountAsync(NotificationTopology.Queue) == 0);
        (await DigestAsync(bob, chat)).ShouldNotBeNull().MessageCount.ShouldBe(1);
        _failures.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("id-mismatch")]
    [InlineData("unknown-routing-key")]
    [InlineData("missing-fields")]
    public async Task PoisonMessage_IsDeadLetteredWithoutRetrying(string kind)
    {
        var registered = new UserRegistered(Guid.CreateVersion7(), Now, Guid.CreateVersion7(), "x@example.com", "X");
        var json = JsonSerializer.Serialize(registered, MessageJson.Options);
        var (exchange, routingKey, messageId, body) = kind switch
        {
            "malformed" => (MessageRoutes.Exchange, MessageRoutes.UserRegistered, registered.MessageId.ToString(), "{not json"),
            "id-mismatch" => (MessageRoutes.Exchange, MessageRoutes.UserRegistered, Guid.CreateVersion7().ToString(), json),
            "unknown-routing-key" => (string.Empty, NotificationTopology.Queue, registered.MessageId.ToString(), json),
            _ => (MessageRoutes.Exchange, MessageRoutes.UserRegistered, registered.MessageId.ToString(),
                JsonSerializer.Serialize(registered with { UserId = Guid.Empty }, MessageJson.Options))
        };

        await PublishRawAsync(exchange, routingKey, messageId, body);

        var dead = await GetAsync(NotificationTopology.DeadLetterQueue);
        Encoding.UTF8.GetString(dead.Body.Span).ShouldBe(body);
        dead.BasicProperties.MessageId.ShouldBe(messageId);
        _failures.Calls.ShouldBe(0);
        (await QueryAsync(db => db.ProcessedMessages.CountAsync())).ShouldBe(0);
        (await CountAsync(NotificationTopology.RetryQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task TransientFailure_GoesThroughTheRetryQueueAndThenSucceeds()
    {
        _failures.Remaining = 2;
        var userId = Guid.CreateVersion7();

        await PublishAsync(new UserRegistered(Guid.CreateVersion7(), Now, userId, "nora@example.com", "Nora"));

        await WaitUntilAsync(() => QueryAsync(db => db.Recipients.AnyAsync(r => r.UserId == userId)));
        _failures.Calls.ShouldBe(3);
        (await CountAsync(NotificationTopology.DeadLetterQueue)).ShouldBe(0u);
        (await CountAsync(NotificationTopology.RetryQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task PersistentFailure_IsDeadLetteredAfterMaxDeliveryAttempts()
    {
        _failures.Remaining = int.MaxValue;
        var registered = new UserRegistered(Guid.CreateVersion7(), Now, Guid.CreateVersion7(), "nora@example.com", "Nora");

        await PublishAsync(registered);

        var dead = await GetAsync(NotificationTopology.DeadLetterQueue);
        dead.BasicProperties.MessageId.ShouldBe(registered.MessageId.ToString());
        dead.BasicProperties.Headers!.ShouldContainKey(EventConsumer.AttemptHeader);
        Convert.ToInt32(dead.BasicProperties.Headers![EventConsumer.AttemptHeader]).ShouldBe(2);
        _failures.Calls.ShouldBe(3);
        (await QueryAsync(db => db.Recipients.CountAsync())).ShouldBe(0);
        Errors.Take().ShouldHaveSingleItem().ShouldContain("failed 3 times; dead-lettering it");
    }

    private Task PublishAsync(IIntegrationEvent integrationEvent) =>
        PublishRawAsync(MessageRoutes.Exchange, MessageRoutes.RoutingKeyOf(integrationEvent), integrationEvent.MessageId.ToString(),
            JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), MessageJson.Options));

    private async Task PublishRawAsync(string exchange, string routingKey, string messageId, string body) =>
        await _channel!.BasicPublishAsync(exchange, routingKey, mandatory: true,
            new BasicProperties { MessageId = messageId, ContentType = MessageJson.ContentType, Persistent = true },
            Encoding.UTF8.GetBytes(body));

    private async Task<uint> CountAsync(string queue) => await _channel!.MessageCountAsync(queue);

    private async Task<BasicGetResult> GetAsync(string queue)
    {
        BasicGetResult? result = null;
        await Eventually.UntilAsync(async () => (result = await _channel!.BasicGetAsync(queue, autoAck: true)) is not null,
            $"No message arrived in {queue}.");
        return result!;
    }

    private static Task WaitUntilAsync(Func<Task<bool>> condition) =>
        Eventually.UntilAsync(condition, "The consumer did not reach the expected state.");
}

public sealed class FlakyProcessor(IIntegrationEventProcessor inner, FlakyProcessor.Failures failures) : IIntegrationEventProcessor
{
    public Task<bool> ProcessAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref failures.CallCount);
        if (Interlocked.Decrement(ref failures.Remaining) >= 0)
            throw new TimeoutException("The notifications database timed out.");
        return inner.ProcessAsync(integrationEvent, cancellationToken);
    }

    public sealed class Failures
    {
        public int Remaining;
        public int CallCount;

        public int Calls => Volatile.Read(ref CallCount);
    }
}
