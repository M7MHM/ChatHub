using ChatHub.Domain.Entities;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class UserProfileTests
{
    [Fact]
    public void Create_WithEmail_CreatesAnOfflineUserWithASecurityStamp()
    {
        var user = UserProfile.Create("  Alice  ", "hash", email: Email.Create("alice@example.com"));

        user.DisplayName.ShouldBe("Alice");
        user.PasswordHash.ShouldBe("hash");
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
        user.IsOnline.ShouldBeFalse();
        user.IsDeleted.ShouldBeFalse();
        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Create_TwoUsers_GetDifferentSecurityStamps()
    {
        TestData.User().SecurityStamp.ShouldNotBe(TestData.User().SecurityStamp);
    }

    [Fact]
    public void Create_WithoutAnyContact_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => UserProfile.Create("Alice", "hash"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankDisplayName_Throws(string displayName)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UserProfile.Create(displayName, "hash", PhoneNumber.Create("+201001234567")));
    }

    [Fact]
    public void Create_DisplayNameLongerThanTheMaximum_Throws()
    {
        var displayName = new string('x', UserProfile.MaxDisplayNameLength + 1);

        Should.Throw<DomainRuleViolationException>(() =>
            UserProfile.Create(displayName, "hash", PhoneNumber.Create("+201001234567")));
    }

    [Fact]
    public void Create_BlankPasswordHash_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            UserProfile.Create("Alice", " ", PhoneNumber.Create("+201001234567")));
    }

    [Fact]
    public void RotateSecurityStamp_Always_IssuesANewStamp()
    {
        var user = TestData.User();
        var before = user.SecurityStamp;

        user.RotateSecurityStamp();

        user.SecurityStamp.ShouldNotBe(before);
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void ChangePassword_NewHash_ReplacesTheHashAndRotatesTheStamp()
    {
        var user = TestData.User();
        var before = user.SecurityStamp;

        user.ChangePassword("new-hash");

        user.PasswordHash.ShouldBe("new-hash");
        user.SecurityStamp.ShouldNotBe(before);
    }

    [Fact]
    public void ChangePassword_DeletedUser_Throws()
    {
        var user = TestData.User();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(() => user.ChangePassword("new-hash"));
    }

    [Fact]
    public void UpdateProfile_NewValues_StoresTrimmedValuesAndRaisesProfileUpdated()
    {
        var user = TestData.User();

        user.UpdateProfile(" Alice A. ", " /api/files/a ", "  hi  ");

        user.DisplayName.ShouldBe("Alice A.");
        user.AvatarUrl.ShouldBe("/api/files/a");
        user.Bio.ShouldBe("hi");
        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserProfileUpdatedEvent(user.Id));
    }

    [Fact]
    public void UpdateProfile_SameValues_RaisesNoEvent()
    {
        var user = TestData.User();

        user.UpdateProfile("Alice", "   ", null);

        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void UpdateProfile_BioLongerThanTheMaximum_Throws()
    {
        var user = TestData.User();

        Should.Throw<DomainRuleViolationException>(() =>
            user.UpdateProfile("Alice", null, new string('x', UserProfile.MaxBioLength + 1)));
    }

    [Fact]
    public void UpdateProfile_DeletedUser_Throws()
    {
        var user = TestData.User();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(() => user.UpdateProfile("Alice", null, null));
    }

    [Fact]
    public void ChangeEmail_RemovingTheOnlyContact_Throws()
    {
        var user = TestData.User();

        Should.Throw<DomainRuleViolationException>(() => user.ChangeEmail(null));
    }

    [Fact]
    public void ChangeEmail_NewAddress_RaisesProfileUpdatedOnlyWhenItChanges()
    {
        var user = TestData.User();
        var email = Email.Create("new@example.com");

        user.ChangeEmail(email);
        user.ChangeEmail(Email.Create("NEW@example.com"));

        user.Email.ShouldBe(email);
        user.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void ChangePhoneNumber_RemovingTheOnlyContact_Throws()
    {
        var user = UserProfile.Create("Bob", "hash", PhoneNumber.Create("+201001234567"));

        Should.Throw<DomainRuleViolationException>(() => user.ChangePhoneNumber(null));
    }

    [Fact]
    public void ChangePhoneNumber_RemovedWhileAnEmailRemains_Succeeds()
    {
        var user = UserProfile.Create("Bob", "hash", PhoneNumber.Create("+201001234567"), Email.Create("bob@example.com"));

        user.ChangePhoneNumber(null);

        user.PhoneNumber.ShouldBeNull();
        user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserProfileUpdatedEvent>();
    }

    [Fact]
    public void GoOnline_OfflineUser_SetsOnlineAndRaisesStatusChanged()
    {
        var user = TestData.User();

        user.GoOnline();

        user.IsOnline.ShouldBeTrue();
        var changed = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserStatusChangedEvent>();
        changed.IsOnline.ShouldBeTrue();
        changed.LastSeenUtc.ShouldBe(user.LastSeenUtc!.Value);
    }

    [Fact]
    public void GoOnline_AlreadyOnline_RaisesNoSecondEvent()
    {
        var user = TestData.User();
        user.GoOnline();

        user.GoOnline();

        user.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void GoOffline_OnlineUser_RecordsLastSeenAndRaisesStatusChanged()
    {
        var user = TestData.User();
        user.GoOnline();
        user.ClearDomainEvents();

        user.GoOffline();

        user.IsOnline.ShouldBeFalse();
        user.LastSeenUtc.ShouldNotBeNull();
        user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserStatusChangedEvent>().IsOnline.ShouldBeFalse();
    }

    [Fact]
    public void GoOffline_AlreadyOffline_RaisesNoEvent()
    {
        var user = TestData.User();

        user.GoOffline();

        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void GoOnline_DeletedUser_Throws()
    {
        var user = TestData.User();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(user.GoOnline);
    }

    [Fact]
    public void Delete_OnlineUser_GoesOfflineMarksDeletedAndRotatesTheStamp()
    {
        var user = TestData.User();
        user.GoOnline();
        user.ClearDomainEvents();
        var before = user.SecurityStamp;

        user.Delete();

        user.IsDeleted.ShouldBeTrue();
        user.IsOnline.ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(before);
        user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<UserStatusChangedEvent>().IsOnline.ShouldBeFalse();
    }
}
