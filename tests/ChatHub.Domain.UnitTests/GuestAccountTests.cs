using ChatHub.Domain.Entities;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class GuestAccountTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateGuest_HasNoContactOrUsablePasswordAndExpiresAfterItsLifetime()
    {
        var guest = UserProfile.CreateGuest(Username.ForSystemAccount("guest_ab12cd34"), "Guest 4821", Now, TimeSpan.FromHours(24));

        (guest.IsGuest, guest.IsDemoPersona, guest.Email, guest.PhoneNumber).ShouldBe((true, false, (Email?)null, (PhoneNumber?)null));
        guest.PasswordHash.ShouldBe(UserProfile.UnusablePasswordHash);
        guest.GuestExpiresAtUtc.ShouldBe(Now.AddHours(24));
        guest.CreatedAtUtc.ShouldBe(Now);
        guest.IsGuestExpired(Now.AddHours(24) - TimeSpan.FromTicks(1)).ShouldBeFalse();
        guest.IsGuestExpired(Now.AddHours(24)).ShouldBeTrue();
        guest.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void CreateGuest_WithoutAPositiveLifetime_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UserProfile.CreateGuest(Username.ForSystemAccount("guest_x1y2z3w4"), "Guest 1", Now, TimeSpan.Zero));
    }

    [Fact]
    public void CreateDemoPersona_KeepsItsFixedIdAndCannotSignIn()
    {
        var id = Guid.Parse("0193a5c0-0000-7000-8000-000000000002");

        var persona = UserProfile.CreateDemoPersona(id, Username.ForSystemAccount("demo_layla"), "Layla Hassan · ليلى حسن", " Designer ");

        (persona.Id, persona.IsDemoPersona, persona.IsGuest, persona.Bio).ShouldBe((id, true, false, "Designer"));
        persona.PasswordHash.ShouldBe(UserProfile.UnusablePasswordHash);
        persona.IsGuestExpired(DateTime.MaxValue).ShouldBeFalse();
    }

    [Fact]
    public void GuestsAndPersonas_CannotChangeUsernameContactDetailsOrPassword()
    {
        var guest = UserProfile.CreateGuest(Username.ForSystemAccount("guest_ab12cd34"), "Guest 4821", Now, TimeSpan.FromHours(24));
        var persona = UserProfile.CreateDemoPersona(Guid.NewGuid(), Username.ForSystemAccount("demo_omar"), "Omar", null);

        foreach (var user in new[] { guest, persona })
        {
            Should.Throw<DomainRuleViolationException>(() => user.ChangeUsername(Username.Create("someone_new"), Now, TimeSpan.Zero));
            Should.Throw<DomainRuleViolationException>(() => user.ChangeEmail(Email.Create("x@example.com")));
            Should.Throw<DomainRuleViolationException>(() => user.ChangePhoneNumber(PhoneNumber.Create("01001234567")));
            Should.Throw<DomainRuleViolationException>(() => user.ChangePassword("hash"));
            Should.Throw<DomainRuleViolationException>(() => user.RequestEmailVerification("t", "h", Now, TimeSpan.FromHours(1)));
            Should.Throw<DomainRuleViolationException>(() => user.RequestPasswordReset("t", "h", Now, TimeSpan.FromHours(1)));
        }
    }

    [Fact]
    public void Guest_CanStillUpdateItsProfileAndDeleteItself()
    {
        var guest = UserProfile.CreateGuest(Username.ForSystemAccount("guest_ab12cd34"), "Guest 4821", Now, TimeSpan.FromHours(24));

        guest.UpdateProfile("Curious Guest", null, null);
        guest.Delete();

        (guest.DisplayName, guest.IsDeleted).ShouldBe(("Curious Guest", true));
    }

    [Theory]
    [InlineData("guest_anything")]
    [InlineData("demo_layla")]
    [InlineData("chathub_bot")]
    [InlineData("Guest_Me")]
    public void Username_SystemPrefixes_AreReservedForUsersButAllowedForSystemAccounts(string name)
    {
        Username.IsReserved(name).ShouldBeTrue();
        Username.TryCreate(name, out _, out var error).ShouldBeFalse();
        error.ShouldBe(Username.ReservedMessage);
        Username.ForSystemAccount(name).Value.ShouldBe(name.ToLowerInvariant());
    }

    [Fact]
    public void Username_ForSystemAccount_StillChecksTheFormat()
    {
        Should.Throw<DomainRuleViolationException>(() => Username.ForSystemAccount("guest__double"));
        Username.Create("guests").Value.ShouldBe("guests");
    }
}
