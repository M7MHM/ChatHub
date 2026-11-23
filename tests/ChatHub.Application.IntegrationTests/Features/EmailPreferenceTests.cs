using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Events;
using ChatHub.Infrastructure.Identity;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class EmailPreferenceTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task UpdateEmailPreferences_TurnsDigestsOffAndOnAndGetMeShowsIt()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");

        (await SendAsync(bob.User.Id, new UpdateEmailPreferencesCommand(false))).ShouldSucceed();
        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailNotificationsEnabled.ShouldBeFalse();
        (await SendAsync(bob.User.Id, new UpdateEmailPreferencesCommand(false))).ShouldSucceed();
        (await SendAsync(bob.User.Id, new UpdateEmailPreferencesCommand(true))).ShouldSucceed();

        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailNotificationsEnabled.ShouldBeTrue();
        DomainEvents.OfType<EmailNotificationsChangedEvent>().Select(e => e.Enabled).ShouldBe([false, true]);
    }

    [Fact]
    public async Task Unsubscribe_ValidToken_TurnsDigestsOffAndRepeatingItIsHarmless()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        var token = UnsubscribeToken();
        (await SendAsync(null, new GetUnsubscribeStatusQuery(token))).ShouldSucceed().AlreadyUnsubscribed.ShouldBeFalse();

        (await SendAsync(null, new UnsubscribeCommand(token))).ShouldSucceed();
        (await SendAsync(null, new UnsubscribeCommand(token))).ShouldSucceed();

        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailNotificationsEnabled.ShouldBeFalse();
        (await SendAsync(null, new GetUnsubscribeStatusQuery(token))).ShouldSucceed().AlreadyUnsubscribed.ShouldBeTrue();
        DomainEvents.OfType<EmailNotificationsChangedEvent>().ShouldHaveSingleItem()
            .ShouldBe(new EmailNotificationsChangedEvent(bob.User.Id, false));
    }

    [Fact]
    public async Task Unsubscribe_UnknownToken_IsInvalid()
    {
        await RegisterAsync("Bob", "bob@example.com");

        (await SendAsync(null, new UnsubscribeCommand(OpaqueToken.Generate()))).ShouldFailWith(UserErrors.InvalidUnsubscribeToken);
        (await SendAsync(null, new GetUnsubscribeStatusQuery(OpaqueToken.Generate())))
            .ShouldFailWith(UserErrors.InvalidUnsubscribeToken);
    }

    [Fact]
    public async Task Unsubscribe_RotatedToken_OnlyTheNewOneWorks()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        var old = UnsubscribeToken();
        var fresh = OpaqueToken.Generate();
        await UseDbAsync(async db =>
        {
            var user = await db.UserProfiles.FindAsync(bob.User.Id);
            user!.IssueUnsubscribeToken(fresh, OpaqueToken.Hash(fresh));
            await db.SaveChangesAsync();
        });

        (await SendAsync(null, new UnsubscribeCommand(old))).ShouldFailWith(UserErrors.InvalidUnsubscribeToken);
        (await SendAsync(null, new UnsubscribeCommand(fresh))).ShouldSucceed();
    }

    [Fact]
    public async Task Unsubscribe_DeletedAccount_IsInvalid()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        var token = UnsubscribeToken();
        (await SendAsync(bob.User.Id, new DeleteMyAccountCommand())).ShouldSucceed();

        (await SendAsync(null, new UnsubscribeCommand(token))).ShouldFailWith(UserErrors.InvalidUnsubscribeToken);
    }

    private string UnsubscribeToken() => DomainEvents.OfType<UnsubscribeTokenIssuedEvent>()[^1].Token;
}
