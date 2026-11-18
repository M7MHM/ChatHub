using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class SecurityTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task Register_NewUsers_EachGetsADistinctSecurityStamp()
    {
        var vera = (await RegisterAsync("Vera", "vera@example.com")).User.Id;
        var walt = (await RegisterAsync("Walt", "walt@example.com")).User.Id;
        var xena = (await RegisterAsync("Xena", "xena@example.com")).User.Id;

        Guid[] stamps = [await StampOfAsync(vera), await StampOfAsync(walt), await StampOfAsync(xena)];

        stamps.ShouldNotContain(Guid.Empty);
        stamps.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task ChangePassword_Success_RotatesTheSecurityStamp()
    {
        var vera = await CreateUserAsync("Vera");
        var before = await StampOfAsync(vera);

        (await SendAsync(vera, new ChangePasswordCommand(Password, "newpass123"))).ShouldSucceed();

        (await StampOfAsync(vera)).ShouldNotBe(before);
    }

    [Fact]
    public async Task DeleteMyAccount_Success_RotatesTheSecurityStamp()
    {
        var xena = await CreateUserAsync("Xena");
        var before = await StampOfAsync(xena);

        (await SendAsync(xena, new DeleteMyAccountCommand())).ShouldSucceed();

        (await StampOfAsync(xena)).ShouldNotBe(before);
    }

    [Theory]
    [InlineData(nameof(ChangePasswordCommand))]
    [InlineData(nameof(DeleteMyAccountCommand))]
    public async Task CredentialChange_Success_DisconnectsTheUsersLiveConnections(string command)
    {
        var zane = await CreateUserAsync("Zane");

        var result = command == nameof(ChangePasswordCommand)
            ? await SendAsync(zane, new ChangePasswordCommand(Password, "newpass123"))
            : await SendAsync(zane, new DeleteMyAccountCommand());

        result.ShouldSucceed();
        SignalR.Disconnects.ShouldBe([zane]);
    }
}
