using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class RecipientTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    [Fact]
    public async Task OlderUserEventArrivingLate_IsIgnored()
    {
        var userId = Guid.CreateVersion7();
        var registeredAt = Now;
        Clock.Advance(TimeSpan.FromMinutes(1));

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, userId, "new@example.com", "New Name"));
        await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), registeredAt, userId, "old@example.com", "Old Name"));

        var recipient = await RecipientAsync(userId);
        (recipient.Email, recipient.DisplayName, recipient.LastEventAtUtc).ShouldBe(("new@example.com", "New Name", Now));
    }

    [Fact]
    public async Task NewerProfileUpdate_ReplacesTheAddressAndName()
    {
        var userId = Guid.CreateVersion7();
        await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), Now, userId, "old@example.com", "Old"));
        Clock.Advance(TimeSpan.FromSeconds(1));

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, userId, null, "Phone Only"));

        var recipient = await RecipientAsync(userId);
        (recipient.Email, recipient.DisplayName, recipient.IsDeleted).ShouldBe((null, "Phone Only", false));
    }

    [Fact]
    public async Task UserEventsAfterDeletion_AreIgnoredEvenIfNewer()
    {
        var userId = Guid.CreateVersion7();
        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, userId));
        Clock.Advance(TimeSpan.FromMinutes(1));

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, userId, "back@example.com", "Back"));

        var recipient = await RecipientAsync(userId);
        (recipient.IsDeleted, recipient.Email).ShouldBe((true, null));
    }

    [Fact]
    public async Task VerificationAndPreference_FollowTheNewestEventLikeTheOtherFields()
    {
        var userId = Guid.CreateVersion7();
        var registeredAt = Now;
        Clock.Advance(TimeSpan.FromMinutes(1));

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, userId, "bob@example.com", "Bob",
            EmailVerified: true, EmailNotificationsEnabled: false));
        await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), registeredAt, userId, "bob@example.com", "Bob",
            EmailVerified: false, EmailNotificationsEnabled: true));

        var recipient = await RecipientAsync(userId);
        (recipient.EmailVerified, recipient.EmailNotificationsEnabled).ShouldBe((true, false));
    }

    [Fact]
    public async Task UnsubscribeLink_IsKeptByEventsWithoutOneAndTheNewestIssuedLinkWinsInAnyArrivalOrder()
    {
        var userId = Guid.CreateVersion7();
        var registeredAt = Now;
        Clock.Advance(TimeSpan.FromMinutes(1));
        var updatedAt = Now;
        Clock.Advance(TimeSpan.FromMinutes(1));
        var rotatedAt = Now;

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), updatedAt, userId, "bob@example.com", "Bob"));
        await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), registeredAt, userId, "bob@example.com", "Bob",
            UnsubscribeLink: "https://api.example.test/api/email/unsubscribe?token=first"));
        (await RecipientAsync(userId)).UnsubscribeLink.ShouldBe("https://api.example.test/api/email/unsubscribe?token=first");

        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), rotatedAt, userId, "bob@example.com", "Bob",
            UnsubscribeLink: "https://api.example.test/api/email/unsubscribe?token=rotated"));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), registeredAt.AddSeconds(1), userId, "bob@example.com", "Bob",
            UnsubscribeLink: "https://api.example.test/api/email/unsubscribe?token=stale"));

        var recipient = await RecipientAsync(userId);
        (recipient.UnsubscribeLink, recipient.UnsubscribeLinkIssuedAtUtc)
            .ShouldBe(("https://api.example.test/api/email/unsubscribe?token=rotated", rotatedAt));
    }

    [Fact]
    public async Task Deletion_ClearsTheUnsubscribeLinkAndTheVerification()
    {
        var userId = await UserAsync("Bob", unsubscribeLink: "https://api.example.test/api/email/unsubscribe?token=x");
        Clock.Advance(TimeSpan.FromSeconds(1));

        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, userId));

        var recipient = await RecipientAsync(userId);
        (recipient.IsDeleted, recipient.EmailVerified, recipient.UnsubscribeLink, recipient.CanReceiveEmailDigests)
            .ShouldBe((true, false, null, false));
    }
}
