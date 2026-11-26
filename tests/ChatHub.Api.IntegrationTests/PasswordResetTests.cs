using System.Diagnostics;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests;

public sealed class PasswordResetTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string NewPassword = "brandnew789";
    private static readonly TimeSpan MinimumResponseTime = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ForgotPassword_KnownUnknownUnverifiedOrDeleted_Returns202WithTheSameBodyAfterTheMinimumTime()
    {
        var bob = await RegisterAsync("Bob");
        await VerifyEmailAsync(bob.Id);
        await RegisterAsync("Una");
        var gone = await RegisterAsync("Gone");
        await VerifyEmailAsync(gone.Id);
        (await Api.DeleteAsync("/api/users/me", gone.Token)).Status.ShouldBe(204);

        var responses = new List<(ApiResponse Response, TimeSpan Took)>();
        foreach (var email in new[] { "bob@example.com", "nobody@example.com", "una@example.com", "gone@example.com" })
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await NewClient().PostAsync("/api/auth/forgot-password", new { email });
            responses.Add((response, stopwatch.Elapsed));
        }

        responses.ShouldAllBe(r => r.Response.Status == 202);
        responses.Select(r => r.Response.Text).Distinct().ShouldHaveSingleItem().ShouldContain("\"message\":");
        responses.ShouldAllBe(r => r.Took >= MinimumResponseTime - TimeSpan.FromMilliseconds(15));
        (await QueryDbAsync(db => db.PasswordResetTokens.Select(t => t.UserId).ToListAsync())).ShouldBe([bob.Id]);
    }

    [Fact]
    public async Task ForgotPassword_MalformedEmail_Returns400()
    {
        var response = await Api.PostAsync("/api/auth/forgot-password", new { email = "nope" });

        response.Status.ShouldBe(400);
        response.HasError("Email").ShouldBeTrue();
    }

    [Fact]
    public async Task ResetPassword_ValidToken_Returns204SignsOutEverywhereAndClosesHubConnections()
    {
        var bob = await RegisterAsync("Bob");
        await VerifyEmailAsync(bob.Id);
        var hub = await ConnectHubAsync(bob);
        var token = await IssueResetTokenAsync(bob.Id);

        var reset = await NewClient().PostAsync("/api/auth/reset-password", new { token, newPassword = NewPassword });

        reset.Status.ShouldBe(204, reset.Text);
        (await hub.WaitForCloseAsync()).ShouldBeTrue();
        (await Api.GetAsync("/api/users/me", bob.Token)).Status.ShouldBe(401);
        (await NewClient().PostAsync("/api/auth/refresh", new { refreshToken = bob.RefreshToken })).Code.ShouldBe("Auth.InvalidRefreshToken");
        (await NewClient().PostAsync("/api/auth/login", new { identifier = "bob@example.com", password = Password })).Status.ShouldBe(400);
        (await LoginAsync("bob@example.com", NewPassword)).Id.ShouldBe(bob.Id);
        var again = await NewClient().PostAsync("/api/auth/reset-password", new { token, newPassword = "another123" });
        (again.Status, again.Code, again.IsProblem).ShouldBe((400, "Auth.ResetTokenUsed", true));
    }

    [Fact]
    public async Task ResetPassword_UnknownExpiredOrWeak_Returns400WithTheReason()
    {
        var bob = await RegisterAsync("Bob");
        await VerifyEmailAsync(bob.Id);
        var expired = await IssueResetTokenAsync(bob.Id, DateTime.UtcNow.AddMinutes(-31));
        var valid = await IssueResetTokenAsync(bob.Id);

        var unknown = await Api.PostAsync("/api/auth/reset-password", new { token = OpaqueToken.Generate(), newPassword = NewPassword });
        var late = await Api.PostAsync("/api/auth/reset-password", new { token = expired, newPassword = NewPassword });
        var weak = await Api.PostAsync("/api/auth/reset-password", new { token = valid, newPassword = "short" });

        (unknown.Status, unknown.Code).ShouldBe((400, "Auth.InvalidResetToken"));
        (late.Status, late.Code).ShouldBe((400, "Auth.ResetTokenExpired"));
        weak.Status.ShouldBe(400);
        weak.HasError("NewPassword").ShouldBeTrue();
        (await LoginAsync("bob@example.com")).Id.ShouldBe(bob.Id);
    }

    private Task VerifyEmailAsync(Guid userId) =>
        QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAtUtc, DateTime.UtcNow)));

    private async Task<string> IssueResetTokenAsync(Guid userId, DateTime? issuedAtUtc = null)
    {
        var token = OpaqueToken.Generate();
        await using var scope = Fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.UserProfiles.SingleAsync(u => u.Id == userId);
        db.PasswordResetTokens.Add(user.RequestPasswordReset(token, OpaqueToken.Hash(token),
            issuedAtUtc ?? DateTime.UtcNow, TimeSpan.FromMinutes(30)));
        await db.SaveChangesAsync();
        return token;
    }
}
