using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class LastSeenPrivacyTests
{
    [Fact]
    public void NewUser_ShowsLastSeenByDefault()
    {
        TestData.User().ShowLastSeen.ShouldBeTrue();
    }

    [Fact]
    public void SetLastSeenVisibility_Change_RaisesOneEventWithTheNewValue()
    {
        var user = TestData.User();

        user.SetLastSeenVisibility(false);

        user.ShowLastSeen.ShouldBeFalse();
        var changed = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<LastSeenVisibilityChangedEvent>();
        (changed.UserId, changed.ShowLastSeen).ShouldBe((user.Id, false));
    }

    [Fact]
    public void SetLastSeenVisibility_SameValue_RaisesNothing()
    {
        var user = TestData.User();

        user.SetLastSeenVisibility(true);

        user.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void SetLastSeenVisibility_DeletedUser_Throws()
    {
        var user = TestData.User();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(() => user.SetLastSeenVisibility(false));
    }
}
