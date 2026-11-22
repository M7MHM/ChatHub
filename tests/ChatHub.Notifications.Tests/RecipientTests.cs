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

    private Task<Recipient> RecipientAsync(Guid userId) =>
        QueryAsync(db => db.Recipients.AsNoTracking().SingleAsync(r => r.UserId == userId));
}
