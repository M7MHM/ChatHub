using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class UsernameTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromDays(14);

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("Alice_99", "alice_99")]
    [InlineData("  Bob.Builder  ", "bob.builder")]
    [InlineData("a.b_c", "a.b_c")]
    [InlineData("abc", "abc")]
    [InlineData("carol_", "carol_")]
    [InlineData("admin1", "admin1")]
    [InlineData("abcdefghijabcdefghijabcdefghij", "abcdefghijabcdefghijabcdefghij")]
    public void Create_ValidInput_IsStoredNormalizedToLowercase(string input, string stored)
    {
        Username.Create(input).Value.ShouldBe(stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("abcdefghijabcdefghijabcdefghijk")]
    [InlineData("1alice")]
    [InlineData("_alice")]
    [InlineData(".alice")]
    [InlineData("al..ice")]
    [InlineData("al__ice")]
    [InlineData("al._ice")]
    [InlineData("alice.")]
    [InlineData("al ice")]
    [InlineData("alice!")]
    [InlineData("al-ice")]
    [InlineData("ålice")]
    [InlineData("@alice")]
    public void Create_MalformedInput_ThrowsWithTheRules(string input)
    {
        Username.TryCreate(input, out var created, out var error).ShouldBeFalse();
        created.ShouldBeNull();
        error.ShouldBe(Username.RulesMessage);
        Should.Throw<DomainRuleViolationException>(() => Username.Create(input)).Message.ShouldBe(Username.RulesMessage);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("support")]
    [InlineData("chathub")]
    [InlineData("system")]
    [InlineData("api")]
    [InlineData("root")]
    [InlineData("help")]
    [InlineData("guest")]
    [InlineData("bot")]
    [InlineData("null")]
    [InlineData("undefined")]
    [InlineData(" Moderator ")]
    public void Create_ReservedName_ThrowsWithTheReservedMessageInAnyCase(string input)
    {
        Username.IsReserved(input).ShouldBeTrue();
        Username.TryCreate(input, out _, out var error).ShouldBeFalse();
        error.ShouldBe(Username.ReservedMessage);
        Should.Throw<DomainRuleViolationException>(() => Username.Create(input)).Message.ShouldBe(Username.ReservedMessage);
    }

    [Fact]
    public void Equality_IgnoresTheCaseOfTheInput()
    {
        Username.Create("Alice").ShouldBe(Username.Create("alice"));
        Username.Create("alice").ShouldNotBe(Username.Create("alice2"));
    }

    [Fact]
    public void ChangeUsername_FirstChange_IsAllowedRecordsTheTimeAndRaisesProfileUpdated()
    {
        var user = TestData.User();
        user.CanChangeUsername(Now, Cooldown).ShouldBeTrue();
        user.NextUsernameChangeAtUtc(Cooldown).ShouldBeNull();

        user.ChangeUsername(Username.Create("new_name"), Now, Cooldown);

        user.Username.Value.ShouldBe("new_name");
        user.UsernameChangedAtUtc.ShouldBe(Now);
        user.NextUsernameChangeAtUtc(Cooldown).ShouldBe(Now + Cooldown);
        user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserProfileUpdatedEvent>().UserId.ShouldBe(user.Id);
    }

    [Fact]
    public void ChangeUsername_WithinTheCooldown_ThrowsAndKeepsTheName()
    {
        var user = TestData.User();
        user.ChangeUsername(Username.Create("first_name"), Now, Cooldown);

        Should.Throw<DomainRuleViolationException>(() =>
            user.ChangeUsername(Username.Create("second_name"), Now + Cooldown - TimeSpan.FromSeconds(1), Cooldown));

        user.Username.Value.ShouldBe("first_name");
        user.CanChangeUsername(Now + Cooldown, Cooldown).ShouldBeTrue();
        user.ChangeUsername(Username.Create("second_name"), Now + Cooldown, Cooldown);
        user.Username.Value.ShouldBe("second_name");
    }

    [Fact]
    public void ChangeUsername_ToTheSameName_DoesNothingEvenDuringTheCooldown()
    {
        var user = TestData.User();
        user.ChangeUsername(Username.Create("same_name"), Now, Cooldown);
        user.ClearDomainEvents();

        user.ChangeUsername(Username.Create("Same_Name"), Now + TimeSpan.FromDays(1), Cooldown);

        user.UsernameChangedAtUtc.ShouldBe(Now);
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void ChangeUsername_WithoutCooldown_CanChangeAgainAtOnce()
    {
        var user = TestData.User();
        user.ChangeUsername(Username.Create("one_name"), Now, TimeSpan.Zero);

        user.ChangeUsername(Username.Create("two_name"), Now, TimeSpan.Zero);

        user.Username.Value.ShouldBe("two_name");
    }

    [Fact]
    public void ChangeUsername_DeletedUser_Throws()
    {
        var user = TestData.User();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(() => user.ChangeUsername(Username.Create("ghost_name"), Now, Cooldown));
    }
}
