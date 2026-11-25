using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Api.IntegrationTests;

[Collection(RabbitMqApiCollection.Name)]
public sealed class MessagingTests(RabbitMqApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Ready_WithRabbitMqConnectedAndAnEmptyOutbox_IsHealthy()
    {
        var response = await WaitForReadinessAsync("Healthy");

        response.Status.ShouldBe(200);
    }

    [Fact]
    public async Task SendingAMessageOverHttp_PublishesMessageSentToTheExchange()
    {
        await using var queue = await fixture.BindQueueAsync(MessageRoutes.MessageSent);
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var direct = await CreateDirectAsync(alice, bob.Id);

        var sent = await SendTextAsync(alice, direct, "hello bob");

        var delivery = await queue.GetAsync();
        delivery.RoutingKey.ShouldBe(MessageRoutes.MessageSent);
        var message = JsonSerializer.Deserialize<MessageSent>(delivery.Body.Span, MessageJson.Options)!;
        delivery.BasicProperties.MessageId.ShouldBe(message.MessageId.ToString());
        message.ChatMessageId.ShouldBe(sent.G("id"));
        message.ConversationId.ShouldBe(direct);
        message.ConversationType.ShouldBe(ConversationType.Direct);
        message.SenderId.ShouldBe(alice.Id);
        message.SenderDisplayName.ShouldBe("Alice");
        message.RecipientUserIds.ShouldBe([bob.Id]);
        message.Preview.ShouldBe("hello bob");
        await WaitUntilAsync(() => QueryDbAsync(db =>
            db.OutboxMessages.AnyAsync(m => m.Id == message.MessageId && m.ProcessedAtUtc != null)));
    }

    [Fact]
    public async Task Ready_WithAnUndeliveredEventOlderThanTheAlert_IsDegraded()
    {
        await WaitForReadinessAsync("Healthy");
        await QueryDbAsync(db =>
        {
            db.OutboxMessages.Add(OutboxMessage.From(
                new UserDeleted(Guid.CreateVersion7(), DateTime.UtcNow.AddMinutes(-10), Guid.NewGuid())));
            return db.SaveChangesAsync();
        });

        var response = await Api.GetAsync("/health/ready");

        response.Status.ShouldBe(200);
        response.Text.ShouldBe("Degraded");
    }

    [Fact]
    public async Task InvalidMessagingSettings_StopTheApiFromStarting()
    {
        await using var factory = Fixture.CreateFactory(new Dictionary<string, string?> { ["Messaging:BatchSize"] = "0" });

        var exception = Should.Throw<Exception>(() => _ = factory.Server);

        exception.ToString().ShouldContain("Messaging BatchSize must be between 1 and 1000");
        TakeLoggedErrors();
    }

    private async Task<ApiResponse> WaitForReadinessAsync(string status)
    {
        ApiResponse? response = null;
        await Eventually.TryAsync(async () => (response = await Api.GetAsync("/health/ready")).Text == status);
        response!.Text.ShouldBe(status);
        return response;
    }

    private static Task WaitUntilAsync(Func<Task<bool>> condition) =>
        Eventually.UntilAsync(condition, "The outbox row was not marked as published.");
}
