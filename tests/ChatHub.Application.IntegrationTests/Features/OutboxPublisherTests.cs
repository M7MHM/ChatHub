using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Infrastructure.Messaging;
using ChatHub.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class OutboxPublisherTests(DatabaseFixture database, RabbitMqFixture rabbit)
    : AppTestBase(database), IClassFixture<RabbitMqFixture>
{
    private const int BatchSize = 10;

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = rabbit.MessagingSettings(BatchSize);

    private OutboxPublisherService Publisher => Services.GetRequiredService<OutboxPublisherService>();

    [Fact]
    public async Task PublishPending_DeliversPersistentJsonWithTheOutboxIdToABoundQueue()
    {
        await using var queue = await TestQueue.BindAsync(rabbit, MessageRoutes.MessageSent);
        var crew = await CreateCrewAsync();
        var sent = await SendTextAsync(crew.Paula, crew.Group, "hello broker");
        var row = await SingleRowAsync();

        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(1);

        var delivery = await queue.GetAsync();
        delivery.Exchange.ShouldBe(MessageRoutes.Exchange);
        delivery.RoutingKey.ShouldBe(MessageRoutes.MessageSent);
        delivery.BasicProperties.MessageId.ShouldBe(row.Id.ToString());
        delivery.BasicProperties.Type.ShouldBe(nameof(MessageSent));
        delivery.BasicProperties.ContentType.ShouldBe(MessageJson.ContentType);
        delivery.BasicProperties.DeliveryMode.ShouldBe(DeliveryModes.Persistent);
        var message = TestQueue.Read<MessageSent>(delivery);
        message.MessageId.ShouldBe(row.Id);
        message.ChatMessageId.ShouldBe(sent.Id);
        message.Preview.ShouldBe("hello broker");

        var published = await SingleRowAsync();
        published.ProcessedAtUtc.ShouldNotBeNull().ShouldBe(Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        published.Attempts.ShouldBe(0);
        published.LockedUntilUtc.ShouldBeNull();
        (await queue.TryGetAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }

    [Fact]
    public async Task PublishPending_UnroutableMessage_IsRetriedWithExponentialBackoffUntilAQueueIsBound()
    {
        await RegisterAsync("Nora", phone: "01001234567");

        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(1);
        var failed = await SingleRowAsync();
        failed.ProcessedAtUtc.ShouldBeNull();
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldNotBeNull().ShouldContain("Unroutable");
        failed.LockedUntilUtc.ShouldNotBeNull().ShouldBe(Clock.GetUtcNow().UtcDateTime.AddSeconds(1), TimeSpan.FromMilliseconds(1));
        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(1);
        var again = await SingleRowAsync();
        again.Attempts.ShouldBe(2);
        again.LockedUntilUtc.ShouldNotBeNull().ShouldBe(Clock.GetUtcNow().UtcDateTime.AddSeconds(2), TimeSpan.FromMilliseconds(1));

        await using var queue = await TestQueue.BindAsync(rabbit, "user.*");
        Clock.Advance(TimeSpan.FromSeconds(2));
        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(1);

        TestQueue.Read<UserRegistered>(await queue.GetAsync()).DisplayName.ShouldBe("Nora");
        var published = await SingleRowAsync();
        published.ProcessedAtUtc.ShouldNotBeNull();
        published.Attempts.ShouldBe(2);
        published.LastError.ShouldNotBeNull();
    }

    [Fact]
    public async Task PublishPending_EventsWithTokensOrAddresses_AreRedactedInTheOutboxOnceTheBrokerHasThem()
    {
        await using var queue = await TestQueue.BindAsync(rabbit, MessageRoutes.UserRegistered,
            MessageRoutes.EmailVerificationRequested, MessageRoutes.MessageSent);
        await RegisterAsync("Nora", email: "nora@example.com");
        var crew = await CreateCrewAsync();
        await SendTextAsync(crew.Paula, crew.Group, "hello broker");

        (await DrainAsync(Publisher)).ShouldBe(3);

        var deliveries = await queue.TakeAllAsync();
        deliveries.Count.ShouldBe(3);
        TestQueue.Read<EmailVerificationRequested>(deliveries.Single(d => d.RoutingKey == MessageRoutes.EmailVerificationRequested))
            .VerificationLink.ShouldContain("/api/auth/verify-email?token=");
        TestQueue.Read<UserRegistered>(deliveries.Single(d => d.RoutingKey == MessageRoutes.UserRegistered))
            .UnsubscribeLink.ShouldNotBeNull().ShouldContain("/api/email/unsubscribe?token=");
        var rows = await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().ToListAsync());
        rows.ShouldAllBe(r => r.ProcessedAtUtc != null);
        rows.Where(r => r.Type != nameof(MessageSent)).Select(r => r.Payload).ShouldBe(["{}", "{}"]);
        rows.Single(r => r.Type == nameof(MessageSent)).Payload.ShouldContain("hello broker");
    }

    [Fact]
    public async Task PublishPending_RowLeasedByAnotherPublisher_IsSkippedUntilTheLeaseExpires()
    {
        await using var queue = await TestQueue.BindAsync(rabbit, "#");
        var ids = await InsertAsync(1);
        var leasedUntil = Clock.GetUtcNow().UtcDateTime.AddSeconds(30);
        await UseDbAsync(db => db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntilUtc, leasedUntil)));

        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(30));
        (await Publisher.PublishPendingAsync(CancellationToken.None)).ShouldBe(1);
        (await queue.GetAsync()).BasicProperties.MessageId.ShouldBe(ids[0].ToString());
    }

    [Fact]
    public async Task TwoPublishersRunningConcurrently_PublishEveryMessageExactlyOnce()
    {
        await using var queue = await TestQueue.BindAsync(rabbit, "#");
        var ids = await InsertAsync(400);
        var first = Services.GetRequiredService<RabbitMqPublisher>();
        await using var second = NewRabbitMqPublisher(rabbit.Port);
        var secondService = ActivatorUtilities.CreateInstance<OutboxPublisherService>(Services, second);
        await Task.WhenAll(first.ConnectAsync(CancellationToken.None), second.ConnectAsync(CancellationToken.None));

        var counts = await Task.WhenAll(Task.Run(() => DrainAsync(Publisher)), Task.Run(() => DrainAsync(secondService)));

        counts.Sum().ShouldBe(ids.Count);
        counts.ShouldAllBe(count => count > 0, "both publishers should have published part of the outbox");
        (await queue.CountAsync()).ShouldBe((uint)ids.Count);
        var deliveredIds = (await queue.TakeAllAsync()).Select(d => Guid.Parse(d.BasicProperties.MessageId!)).ToList();
        deliveredIds.ShouldBe(ids, ignoreOrder: true);
        (await QueryDbAsync(db => db.OutboxMessages.CountAsync(m => m.ProcessedAtUtc == null || m.Attempts > 0))).ShouldBe(0);
    }

    [Fact]
    public async Task PublishPending_RabbitMqUnreachable_ThrowsWithoutClaimingAnyRow()
    {
        await InsertAsync(3);
        await using var unreachable = NewRabbitMqPublisher(FreePort());
        var service = ActivatorUtilities.CreateInstance<OutboxPublisherService>(Services, unreachable);

        await Should.ThrowAsync<Exception>(() => service.PublishPendingAsync(CancellationToken.None));

        var rows = await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().ToListAsync());
        rows.ShouldAllBe(m => m.ProcessedAtUtc == null && m.Attempts == 0 && m.LockedUntilUtc == null);
        unreachable.IsConnected.ShouldBeFalse();
        unreachable.LastConnectionError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BackgroundPublisher_RabbitMqDown_KeepsRowsPendingAndPublishesThemAfterItComesBack()
    {
        await using var queue = await TestQueue.BindDurableAsync(rabbit, MessageRoutes.MessageSent);
        var crew = await CreateCrewAsync();
        await using var connection = NewRabbitMqPublisher(rabbit.Port);
        var service = ActivatorUtilities.CreateInstance<OutboxPublisherService>(Services, TimeProvider.System, connection);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(connection.IsConnected), "the publisher never connected");
            await rabbit.Container.StopAsync();

            var sent = await SendTextAsync(crew.Paula, crew.Group, "sent while the broker is down");
            await Task.Delay(TimeSpan.FromSeconds(3));
            (await SingleRowAsync()).ProcessedAtUtc.ShouldBeNull();
            connection.IsConnected.ShouldBeFalse();

            await rabbit.Container.StartAsync();
            await WaitUntilAsync(async () => (await SingleRowAsync()).ProcessedAtUtc is not null,
                "the pending row was not published after RabbitMQ came back", TimeSpan.FromSeconds(30));

            await queue.ReconnectAsync();
            TestQueue.Read<MessageSent>(await queue.GetAsync()).ChatMessageId.ShouldBe(sent.Id);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyProcessedRowsOlderThanTheRetention()
    {
        var ids = await InsertAsync(3);
        var now = Clock.GetUtcNow().UtcDateTime;
        await SetProcessedAsync(ids[0], now.AddDays(-3).AddMinutes(-1));
        await SetProcessedAsync(ids[1], now.AddDays(-3).AddMinutes(1));

        (await Publisher.CleanupAsync(CancellationToken.None)).ShouldBe(1);

        (await QueryDbAsync(db => db.OutboxMessages.Select(m => m.Id).ToListAsync())).ShouldBe([ids[1], ids[2]], ignoreOrder: true);
    }

    private RabbitMqPublisher NewRabbitMqPublisher(int port)
    {
        var settings = Services.GetRequiredService<IOptions<MessagingSettings>>().Value;
        return new RabbitMqPublisher(
            Options.Create(new MessagingSettings
            {
                Enabled = true,
                Host = settings.Host,
                Port = port,
                User = settings.User,
                Password = settings.Password,
                PublishTimeoutSeconds = settings.PublishTimeoutSeconds
            }),
            ServerInstance.Create(),
            NullLogger<RabbitMqPublisher>.Instance);
    }

    private async Task<List<Guid>> InsertAsync(int count)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var rows = Enumerable.Range(0, count)
            .Select(i => OutboxMessage.From(new UserDeleted(Guid.CreateVersion7(), now.AddTicks(i * 10), Guid.NewGuid())))
            .ToList();
        await UseDbAsync(async db =>
        {
            db.OutboxMessages.AddRange(rows);
            await db.SaveChangesAsync();
        });
        return [.. rows.Select(r => r.Id)];
    }

    private Task SetProcessedAsync(Guid id, DateTime processedAtUtc) =>
        UseDbAsync(db => db.OutboxMessages.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAtUtc, processedAtUtc)));

    private Task<OutboxMessage> SingleRowAsync() =>
        QueryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync());

    private static async Task<int> DrainAsync(OutboxPublisherService publisher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await publisher.PublishPendingAsync(CancellationToken.None)) > 0) total += claimed;
        return total;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new ShouldAssertException(failure);
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
