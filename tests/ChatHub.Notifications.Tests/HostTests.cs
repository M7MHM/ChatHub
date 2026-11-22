using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Notifications.Tests;

public sealed class HostTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    [Fact]
    public async Task Ready_WithTheDatabaseAndRabbitMqUp_IsHealthy()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.GetStringAsync("/health/live")).ShouldBe("Healthy");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var ready = await client.GetAsync("/health/ready");
        while (await ready.Content.ReadAsStringAsync() != "Healthy" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            ready = await client.GetAsync("/health/ready");
        }

        ((int)ready.StatusCode).ShouldBe(200);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Theory]
    [InlineData("Notifications:AppBaseUrl", "not a url", "AppBaseUrl must be an absolute http(s) URL")]
    [InlineData("Notifications:LeaseSeconds", "30", "LeaseSeconds must be at least twice Smtp TimeoutSeconds")]
    [InlineData("RabbitMq:Password", "", "RabbitMq requires a User and a Password")]
    [InlineData("Smtp:FromAddress", "nobody", "Smtp requires a valid FromAddress")]
    public async Task InvalidSettings_StopTheServiceFromStarting(string key, string value, string error)
    {
        await using var factory = Factory(new Dictionary<string, string?> { [key] = value });

        Should.Throw<Exception>(() => factory.CreateClient()).ToString().ShouldContain(error);
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyRowsPastTheirRetention()
    {
        var now = Now;
        var userId = Guid.CreateVersion7();
        await QueryAsync(async db =>
        {
            db.ProcessedMessages.AddRange(
                new ProcessedMessage { MessageId = Guid.CreateVersion7(), ProcessedAtUtc = now.AddDays(-8) },
                new ProcessedMessage { MessageId = Guid.CreateVersion7(), ProcessedAtUtc = now.AddDays(-6) });
            db.SentNotifications.AddRange(
                new SentNotification { UserId = userId, ConversationId = Guid.CreateVersion7(), Channel = "email", SentAtUtc = now.AddDays(-8) },
                new SentNotification { UserId = userId, ConversationId = Guid.CreateVersion7(), Channel = "email", SentAtUtc = now });
            db.ConversationReads.AddRange(
                new ConversationReadMarker { UserId = userId, ConversationId = Guid.CreateVersion7(), ReadUpToUtc = now.AddDays(-8) },
                new ConversationReadMarker { UserId = userId, ConversationId = Guid.CreateVersion7(), ReadUpToUtc = now.AddHours(-1) });
            db.DigestCooldowns.AddRange(
                new DigestCooldown { UserId = userId, ConversationId = Guid.CreateVersion7(), LastSentAtUtc = now.AddMinutes(-16) },
                new DigestCooldown { UserId = userId, ConversationId = Guid.CreateVersion7(), LastSentAtUtc = now.AddMinutes(-14) });
            return await db.SaveChangesAsync();
        });

        var result = await Services.GetRequiredService<RetentionCleanupService>().CleanupAsync(CancellationToken.None);

        result.ShouldBe(new CleanupResult(1, 1, 1, 1));
        (await QueryAsync(db => db.ProcessedMessages.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.ConversationReads.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.DigestCooldowns.CountAsync())).ShouldBe(1);
    }

    private WebApplicationFactory<Program> Factory(IReadOnlyDictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in BaseSettings(Fixture).Concat(overrides ?? new Dictionary<string, string?>()))
                builder.UseSetting(key, value);
        });
}
