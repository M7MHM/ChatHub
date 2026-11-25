using System.Text.Json;
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
        HttpResponseMessage ready = null!;
        await Eventually.TryAsync(async () =>
            StatusOf(await (ready = await client.GetAsync("/health/ready")).Content.ReadAsStringAsync()) == "Healthy");

        ((int)ready.StatusCode).ShouldBe(200);
        using var report = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        report.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        var checks = report.RootElement.GetProperty("checks");
        checks.EnumerateObject().Select(c => c.Name).ShouldBe(["database", "rabbitmq", "sending"], ignoreOrder: true);
        var sending = checks.GetProperty("sending").GetProperty("data");
        sending.GetProperty("sentToday").GetInt32().ShouldBe(0);
        sending.GetProperty("maxEmailsPerDay").GetInt32().ShouldBe(100000);
        sending.GetProperty("digestsWaiting").GetInt32().ShouldBe(0);
    }

    private static string? StatusOf(string body)
    {
        try
        {
            using var report = JsonDocument.Parse(body);
            return report.RootElement.GetProperty("status").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
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

    [Fact]
    public async Task Cleanup_DeletesMessageSnapshotsAndFailedEmailsPastTheRetentionButKeepsPendingEmails()
    {
        var now = Now;
        var conversationId = Guid.CreateVersion7();
        await QueryAsync(async db =>
        {
            db.MessageSnapshots.AddRange(
                new MessageSnapshot { ChatMessageId = Guid.CreateVersion7(), ConversationId = conversationId, RecordedAtUtc = now.AddDays(-8) },
                new MessageSnapshot { ChatMessageId = Guid.CreateVersion7(), ConversationId = conversationId, RecordedAtUtc = now.AddDays(-1) });
            db.OutgoingEmails.AddRange(
                Email(createdAtUtc: now.AddDays(-9), failedAtUtc: now.AddDays(-8)),
                Email(createdAtUtc: now.AddDays(-2), failedAtUtc: now.AddDays(-1)),
                Email(createdAtUtc: now.AddDays(-9), failedAtUtc: null));
            return await db.SaveChangesAsync();
        });

        var result = await Services.GetRequiredService<RetentionCleanupService>().CleanupAsync(CancellationToken.None);

        (result.MessageSnapshots, result.FailedEmails).ShouldBe((1, 1));
        (await QueryAsync(db => db.MessageSnapshots.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(2);
    }

    private static OutgoingEmail Email(DateTime createdAtUtc, DateTime? failedAtUtc) => new()
    {
        UserId = Guid.CreateVersion7(),
        Template = OutgoingEmailTemplate.EmailVerification,
        ToAddress = "bob@example.com",
        DisplayName = "Bob",
        Link = "https://api.chat.example.test/api/auth/verify-email?token=x",
        ExpiresAtUtc = createdAtUtc.AddHours(24),
        CreatedAtUtc = createdAtUtc,
        FailedAtUtc = failedAtUtc
    };

    private WebApplicationFactory<Program> Factory(IReadOnlyDictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in BaseSettings(Fixture).Concat(overrides ?? new Dictionary<string, string?>()))
                builder.UseSetting(key, value);
        });
}
