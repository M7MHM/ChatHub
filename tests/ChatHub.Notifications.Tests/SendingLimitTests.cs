using System.Globalization;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Health;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatHub.Notifications.Tests;

public sealed class SendingLimitTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    private readonly Dictionary<string, string?> _limits = new();

    protected override IReadOnlyDictionary<string, string?> Settings => _limits;

    [Fact]
    public async Task MinuteLimit_SendsUpToItAndLeavesTheRestPendingUntouchedUntilTheWindowFrees()
    {
        await LimitAsync(("MaxEmailsPerMinute", 3), ("MaxDigestsPerMinute", 3));
        await DueDigestsAsync(5);

        (await DispatchAsync()).ShouldBe(3);
        Channel.Sent.Count.ShouldBe(3);
        var waiting = await DigestsAsync();
        waiting.Count.ShouldBe(2);
        waiting.ShouldAllBe(d => d.Attempts == 0 && d.LastError == null && d.FailedAtUtc == null && d.LockedUntilUtc == null);
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromSeconds(59));
        (await DispatchAsync()).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(2);

        Channel.Sent.Count.ShouldBe(5);
    }

    [Fact]
    public async Task DailyLimit_CountsTheSendsMadeBeforeARestartAndResumesTheNextUtcDay()
    {
        await LimitAsync(("MaxEmailsPerDay", 4), ("MaxDigestsPerDay", 4));
        await DueDigestsAsync(6);
        (await DispatchAsync()).ShouldBe(4);

        await RestartAsync();
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromHours(6));
        (await DispatchAsync()).ShouldBe(0);

        Clock.SetUtcNow(new DateTimeOffset(Now.Date.AddDays(1), TimeSpan.Zero));
        (await DispatchAsync()).ShouldBe(2);
        Channel.Sent.Count.ShouldBe(6);
        (await DigestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DigestDailyCap_KeepsTheRestOfTheDayForVerificationEmails()
    {
        await LimitAsync(("MaxEmailsPerDay", 5), ("MaxDigestsPerDay", 3));
        await DueDigestsAsync(4);
        (await DispatchAsync()).ShouldBe(3);

        await ProcessAsync(Verification("new@example.com"));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.ShouldHaveSingleItem();
        Channel.Sent.Count.ShouldBe(3);
        (await DigestsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task VerificationEmail_GoesFirstWhenTheMinuteIsNearlyUsedUp()
    {
        await LimitAsync(("MaxEmailsPerMinute", 2), ("MaxDigestsPerMinute", 2));
        await DueDigestsAsync(3);
        await ProcessAsync(Verification("new@example.com"));

        (await DispatchAsync()).ShouldBe(2);

        Channel.Verifications.ShouldHaveSingleItem();
        Channel.Sent.Count.ShouldBe(1);
        (await DigestsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task DigestShareOfTheMinute_LeavesRoomForAVerificationEmailArrivingMidBurst()
    {
        await LimitAsync(("MaxEmailsPerMinute", 3), ("MaxDigestsPerMinute", 2));
        await DueDigestsAsync(3);
        (await DispatchAsync()).ShouldBe(2);

        await ProcessAsync(Verification("new@example.com"));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.ShouldHaveSingleItem();
        Channel.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PerRecipientHourlyCap_HoldsExtraDigestsAcrossConversationsUntilTheHourFrees()
    {
        await LimitAsync(("MaxEmailsPerRecipientPerHour", 2));
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        for (var i = 0; i < 3; i++)
            await ProcessAsync(Message(Guid.CreateVersion7(), alice, "Alice", [bob]));
        Clock.Advance(Delay);

        (await DispatchAsync()).ShouldBe(3);
        Channel.Sent.Count.ShouldBe(2);
        var held = (await DigestsAsync()).ShouldHaveSingleItem();
        (held.LockedUntilUtc, held.Attempts, held.LastError).ShouldBe((Now + TimeSpan.FromHours(1), 0, (string?)null));

        Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.Count.ShouldBe(3);
    }

    [Fact]
    public async Task HealthData_ShowsUsageAndTurnsDegradedOnlyWhenADueEmailWaitedTooLong()
    {
        await LimitAsync(("MaxEmailsPerMinute", 1), ("MaxDigestsPerMinute", 1), ("SendBacklogAlertMinutes", 1));
        await DueDigestsAsync(2);
        await DispatchAsync();

        var healthy = await HealthAsync();
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        (healthy.Data["sentLastMinute"], healthy.Data["digestsToday"], healthy.Data["transactionalToday"], healthy.Data["digestsWaiting"])
            .ShouldBe((1, 1, 0, 1));

        Clock.Advance(TimeSpan.FromMinutes(2));
        var degraded = await HealthAsync();

        degraded.Status.ShouldBe(HealthStatus.Degraded);
        ((int)degraded.Data["longestWaitSeconds"]).ShouldBe(120);
        degraded.Data["sentLastMinute"].ShouldBe(0);
    }

    private async Task LimitAsync(params (string Key, int Value)[] limits)
    {
        foreach (var (key, value) in limits)
            _limits[$"Notifications:{key}"] = value.ToString(CultureInfo.InvariantCulture);
        await RestartAsync();
    }

    private async Task DueDigestsAsync(int recipients)
    {
        var alice = await UserAsync("Alice");
        for (var i = 0; i < recipients; i++)
            await ProcessAsync(Message(Guid.CreateVersion7(), alice, "Alice", [await UserAsync($"User{i}")]));
        Clock.Advance(Delay);
    }

    private EmailVerificationRequested Verification(string email) =>
        new(Guid.CreateVersion7(), Now, Guid.CreateVersion7(), email, "Newcomer",
            "https://api.chat.example.test/api/auth/verify-email?token=abc", Now + TimeSpan.FromHours(24));

    private Task<HealthCheckResult> HealthAsync()
    {
        var check = ActivatorUtilities.CreateInstance<SendingHealthCheck>(Services);
        return check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("sending", check, HealthStatus.Degraded, null)
        });
    }
}
