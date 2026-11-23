using System.Data.Common;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Notifications.Tests;

public sealed class VerificationEmailTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    private readonly CrashAfterSend _crash = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.ConfigureDbContext<NotificationsDbContext>(options => options.AddInterceptors(_crash));

    [Fact]
    public async Task EmailVerificationRequested_IsQueuedAndTheNextDispatchSendsItAndLogsItAsTransactional()
    {
        var bob = await UserAsync("Bob", verified: false);
        var request = Request(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();
        Channel.Verifications.ShouldBeEmpty();
        var queued = await QueryAsync(db => db.OutgoingEmails.AsNoTracking().SingleAsync());
        (queued.UserId, queued.Template, queued.ToAddress, queued.Link, queued.ExpiresAtUtc, queued.Attempts)
            .ShouldBe((bob, OutgoingEmailTemplate.EmailVerification, "bob@example.com", request.VerificationLink, request.ExpiresAtUtc, 0));

        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.ShouldHaveSingleItem()
            .ShouldBe(new EmailVerificationNotification(bob, "bob@example.com", "Bob", request.VerificationLink, request.ExpiresAtUtc));
        Channel.Sent.ShouldBeEmpty();
        var log = await QueryAsync(db => db.SentNotifications.AsNoTracking().SingleAsync());
        (log.UserId, log.ConversationId, log.Channel, log.Kind, log.MessageCount, log.SentAtUtc)
            .ShouldBe((bob, (Guid?)null, "fake", NotificationKind.Transactional, 0, Now));
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task EmailVerificationRequested_BeforeTheRegistrationEvent_GoesToTheAddressInTheEvent()
    {
        var userId = Guid.CreateVersion7();

        (await ProcessAsync(Request(userId, "new@example.com", "Newcomer"))).ShouldBeTrue();
        await DispatchAsync();

        var sent = Channel.Verifications.ShouldHaveSingleItem();
        (sent.UserId, sent.Email, sent.DisplayName).ShouldBe((userId, "new@example.com", "Newcomer"));
        (await QueryAsync(db => db.Recipients.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task EmailVerificationRequested_DeliveredTwice_IsQueuedAndSentOnce()
    {
        var bob = await UserAsync("Bob", verified: false);
        var request = Request(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();
        (await ProcessAsync(request)).ShouldBeFalse();
        await DispatchAsync();
        await DispatchAsync();

        Channel.Verifications.ShouldHaveSingleItem();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task EmailVerificationRequested_WithEmailNotificationsOff_IsStillSent()
    {
        var bob = await UserAsync("Bob", verified: false, notifications: false);

        (await ProcessAsync(Request(bob, "bob@example.com"))).ShouldBeTrue();
        await DispatchAsync();

        Channel.Verifications.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EmailVerificationRequested_ForADeletedUserOrWithAnExpiredLink_IsNotQueued()
    {
        var gone = await UserAsync("Gone", verified: false);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, gone));
        var bob = await UserAsync("Bob", verified: false);

        (await ProcessAsync(Request(gone, "gone@example.com"))).ShouldBeTrue();
        (await ProcessAsync(Request(bob, "bob@example.com", expiresIn: TimeSpan.Zero))).ShouldBeTrue();
        await DispatchAsync();

        Channel.Verifications.ShouldBeEmpty();
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task QueuedEmail_WhoseLinkExpiresBeforeItIsSent_IsDroppedWithoutSending()
    {
        var bob = await UserAsync("Bob", verified: false);
        await ProcessAsync(Request(bob, "bob@example.com", expiresIn: TimeSpan.FromMinutes(1)));
        Clock.Advance(TimeSpan.FromMinutes(1));

        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.ShouldBeEmpty();
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task SendFailure_IsRetriedWithBackoffAndThenSent()
    {
        var bob = await UserAsync("Bob", verified: false);
        await ProcessAsync(Request(bob, "bob@example.com"));
        Channel.FailNext(1);

        (await DispatchAsync()).ShouldBe(1);
        var failed = await QueryAsync(db => db.OutgoingEmails.AsNoTracking().SingleAsync());
        (failed.Attempts, failed.LastError, failed.LockedUntilUtc, failed.FailedAtUtc)
            .ShouldBe((1, "SMTP relay unavailable", Now + TimeSpan.FromSeconds(30), (DateTime?)null));
        (await DispatchAsync()).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(30));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.ShouldHaveSingleItem();
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task CrashAfterSmtpAcceptedTheEmail_SendsItAgainAfterTheLeaseButRecordsItOnce()
    {
        var bob = await UserAsync("Bob", verified: false);
        await ProcessAsync(Request(bob, "bob@example.com"));
        _crash.Armed = true;

        await Should.ThrowAsync<InvalidOperationException>(DispatchAsync);
        Channel.Verifications.Count.ShouldBe(1);
        var leased = await QueryAsync(db => db.OutgoingEmails.AsNoTracking().SingleAsync());
        leased.LockedUntilUtc.ShouldBe(Now + TimeSpan.FromSeconds(120));
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
        (await DispatchAsync()).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(120));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Verifications.Count.ShouldBe(2);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    private EmailVerificationRequested Request(Guid userId, string email, string name = "Bob", TimeSpan? expiresIn = null) =>
        new(Guid.CreateVersion7(), Now, userId, email, name,
            $"https://api.chat.example.test/api/auth/verify-email?token={Guid.NewGuid():N}",
            Now + (expiresIn ?? TimeSpan.FromHours(24)));

    private sealed class CrashAfterSend : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("DELETE FROM \"OutgoingEmails\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("Simulated crash between the SMTP send and the commit.");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
