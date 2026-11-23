using System.Text;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Messaging;

namespace ChatHub.Notifications.Tests;

public sealed class EventContractTests
{
    [Theory]
    [InlineData(MessageRoutes.UserRegistered)]
    [InlineData(MessageRoutes.UserProfileUpdated)]
    public void UserEventWrittenBeforeTheNewFields_ReadsAsUnverifiedWithNotificationsOnAndNoLink(string routingKey)
    {
        var id = Guid.CreateVersion7();
        var json = $$"""
            {"messageId":"{{id}}","occurredAtUtc":"2026-11-22T10:00:00Z","userId":"{{Guid.CreateVersion7()}}",
             "email":"bob@example.com","displayName":"Bob"}
            """;

        var result = IntegrationEventReader.Read(routingKey, id.ToString(), Encoding.UTF8.GetBytes(json));

        result.Error.ShouldBeNull();
        var (verified, enabled, link) = result.Event switch
        {
            UserRegistered e => (e.EmailVerified, e.EmailNotificationsEnabled, e.UnsubscribeLink),
            UserProfileUpdated e => (e.EmailVerified, e.EmailNotificationsEnabled, e.UnsubscribeLink),
            _ => throw new ShouldAssertException("Unexpected event type.")
        };
        (verified, enabled, link).ShouldBe((false, true, (string?)null));
    }

    [Theory]
    [InlineData("not-an-email", "https://api.chat.example.test/api/auth/verify-email?token=x")]
    [InlineData("bob@example.com", "javascript:alert(1)")]
    [InlineData("bob@example.com", "ftp://api.chat.example.test/x")]
    public void EmailVerificationRequested_WithAnInvalidAddressOrLink_IsRejected(string email, string link)
    {
        var e = new EmailVerificationRequested(Guid.CreateVersion7(), DateTime.UtcNow, Guid.CreateVersion7(), email, "Bob", link,
            DateTime.UtcNow.AddHours(24));
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e, MessageJson.Options);

        var result = IntegrationEventReader.Read(MessageRoutes.EmailVerificationRequested, e.MessageId.ToString(), body);

        result.Event.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldStartWith("Invalid EmailVerificationRequested");
    }

    [Fact]
    public void EmailVerificationRequested_HasItsOwnRoutingKeyBoundToTheNotificationQueue()
    {
        MessageRoutes.EventTypeOf("user.email-verification-requested").ShouldBe(typeof(EmailVerificationRequested));
        NotificationTopology.RoutingKeys.ShouldContain(MessageRoutes.EmailVerificationRequested);
    }
}
