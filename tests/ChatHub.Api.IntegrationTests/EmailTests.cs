using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.WebAPI.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests;

public sealed class EmailTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task RequestEmailVerification_Returns202ThenA429ProblemOnceTheHourlyLimitIsReached()
    {
        var bob = await RegisterAsync("Bob");

        var first = await Api.PostAsync("/api/auth/email-verification", null, bob.Token);
        var second = await Api.PostAsync("/api/auth/email-verification", null, bob.Token);
        var third = await Api.PostAsync("/api/auth/email-verification", null, bob.Token);

        first.Status.ShouldBe(202, first.Text);
        first.Json.S("email").ShouldBe("bob@example.com");
        first.Json.P("expiresAtUtc").ValueKind.ShouldBe(JsonValueKind.String);
        second.Status.ShouldBe(202);
        third.Status.ShouldBe(429);
        third.IsProblem.ShouldBeTrue();
        third.Code.ShouldBe("Auth.VerificationThrottled");
    }

    [Fact]
    public async Task RequestEmailVerification_AnonymousOrPhoneOnly_IsRejected()
    {
        var omar = await RegisterAsync("Omar", phone: "01001234567");

        (await Api.PostAsync("/api/auth/email-verification", null)).Status.ShouldBe(401);
        var phoneOnly = await Api.PostAsync("/api/auth/email-verification", null, omar.Token);
        phoneOnly.Status.ShouldBe(400);
        phoneOnly.Code.ShouldBe("Auth.NoEmailToVerify");
    }

    [Fact]
    public async Task VerifyEmailPage_ValidToken_ShowsASelfContainedSuccessPageAndMeIsVerified()
    {
        var bob = await RegisterAsync("Bob");
        var token = await IssueVerificationTokenAsync(bob.Id);

        var page = await Api.GetAsync($"/api/auth/verify-email?token={token}");

        page.Status.ShouldBe(200);
        page.Message.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        page.Text.ShouldContain("<h1>Email verified</h1>");
        page.Header("Content-Security-Policy").ShouldBe(HtmlPage.ContentSecurityPolicy);
        page.Header("Cache-Control").ShouldBe("no-store");
        page.Header("Referrer-Policy").ShouldBe("no-referrer");
        page.Text.ShouldNotContain("<script");
        page.Text.ShouldNotContain("src=");
        page.Text.ShouldNotContain("http");
        (await Api.GetAsync("/api/users/me", bob.Token)).Json.P("emailVerified").GetBoolean().ShouldBeTrue();

        var again = await Api.GetAsync($"/api/auth/verify-email?token={token}");
        again.Status.ShouldBe(200);
        again.Text.ShouldContain("<h1>Already verified</h1>");
    }

    [Fact]
    public async Task VerifyEmailPage_ExpiredUnknownMissingOrOversizedToken_ShowsAnErrorPage()
    {
        var bob = await RegisterAsync("Bob");
        var expired = await IssueVerificationTokenAsync(bob.Id, DateTime.UtcNow.AddHours(-25));

        var expiredPage = await Api.GetAsync($"/api/auth/verify-email?token={expired}");
        var unknownPage = await Api.GetAsync($"/api/auth/verify-email?token={OpaqueToken.Generate()}");
        var missingPage = await Api.GetAsync("/api/auth/verify-email");
        var oversizedPage = await Api.GetAsync($"/api/auth/verify-email?token={new string('a', 300)}");

        (expiredPage.Status, unknownPage.Status, missingPage.Status, oversizedPage.Status).ShouldBe((410, 400, 400, 400));
        expiredPage.Text.ShouldContain("<h1>Link expired</h1>");
        new[] { unknownPage, missingPage, oversizedPage }.ShouldAllBe(p => p.Text.Contains("<h1>Invalid link</h1>"));
        (await Api.GetAsync("/api/users/me", bob.Token)).Json.P("emailVerified").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyEmail_PostForTheFrontend_Returns204ThenA409Problem()
    {
        var bob = await RegisterAsync("Bob");
        var token = await IssueVerificationTokenAsync(bob.Id);

        var verified = await Api.PostAsync("/api/auth/verify-email", new { token });
        var again = await Api.PostAsync("/api/auth/verify-email", new { token });
        var empty = await Api.PostAsync("/api/auth/verify-email", new { token = "" });

        verified.Status.ShouldBe(204);
        (again.Status, again.Code).ShouldBe((409, "Auth.EmailAlreadyVerified"));
        empty.Status.ShouldBe(400);
        empty.HasError("Token").ShouldBeTrue();
    }

    [Fact]
    public async Task EmailPreferences_Put_TurnsDigestsOffAndMeShowsIt()
    {
        var bob = await RegisterAsync("Bob");
        var me = (await Api.GetAsync("/api/users/me", bob.Token)).Json;
        (me.P("emailVerified").GetBoolean(), me.P("emailNotificationsEnabled").GetBoolean()).ShouldBe((false, true));

        var response = await Api.PutAsync("/api/users/me/email-preferences", new { emailNotificationsEnabled = false }, bob.Token);

        response.Status.ShouldBe(204);
        (await Api.GetAsync("/api/users/me", bob.Token)).Json.P("emailNotificationsEnabled").GetBoolean().ShouldBeFalse();
        (await Api.PutAsync("/api/users/me/email-preferences", new { emailNotificationsEnabled = true }, null)).Status.ShouldBe(401);
    }

    [Fact]
    public async Task UnsubscribePage_Get_ShowsAOneClickFormAndChangesNothing()
    {
        var bob = await RegisterAsync("Bob");
        var token = await IssueUnsubscribeTokenAsync(bob.Id);

        var page = await Api.GetAsync($"/api/email/unsubscribe?token={token}");

        page.Status.ShouldBe(200);
        page.Text.ShouldContain($"<form method=\"post\" action=\"?token={token}\">");
        page.Text.ShouldContain("<input type=\"hidden\" name=\"List-Unsubscribe\" value=\"One-Click\">");
        page.Header("Content-Security-Policy").ShouldBe(HtmlPage.ContentSecurityPolicy);
        (await Api.GetAsync("/api/users/me", bob.Token)).Json.P("emailNotificationsEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Unsubscribe_OneClickPost_TurnsDigestsOffAndRepeatingItIsHarmless()
    {
        var bob = await RegisterAsync("Bob");
        var token = await IssueUnsubscribeTokenAsync(bob.Id);

        var first = await OneClickAsync(token);
        var second = await OneClickAsync(token);

        (first.Status, second.Status).ShouldBe((200, 200));
        first.Text.ShouldContain("<h1>You are unsubscribed</h1>");
        (await Api.GetAsync("/api/users/me", bob.Token)).Json.P("emailNotificationsEnabled").GetBoolean().ShouldBeFalse();
        (await Api.GetAsync($"/api/email/unsubscribe?token={token}")).Text.ShouldContain("<h1>Already unsubscribed</h1>");
    }

    [Fact]
    public async Task Unsubscribe_UnknownToken_ShowsAnErrorPageForGetAndPost()
    {
        var token = OpaqueToken.Generate();

        var page = await Api.GetAsync($"/api/email/unsubscribe?token={token}");
        var post = await OneClickAsync(token);
        var missing = await Api.GetAsync("/api/email/unsubscribe");

        (page.Status, post.Status, missing.Status).ShouldBe((400, 400, 400));
        new[] { page, post, missing }.ShouldAllBe(p => p.Text.Contains("<h1>Invalid link</h1>"));
    }

    [Fact]
    public void HtmlPage_Render_EncodesTheTitleTheMessageAndTheFormValues()
    {
        var html = HtmlPage.Render("<Title>", "Tom & \"Jerry\" <script>alert(1)</script>",
            new HtmlForm("?token=a\"b", "Go <now>", new Dictionary<string, string> { ["x\"y"] = "<v>" }));

        html.ShouldContain("<h1>&lt;Title&gt;</h1>");
        html.ShouldContain("Tom &amp; &quot;Jerry&quot; &lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("action=\"?token=a&quot;b\"");
        html.ShouldContain("name=\"x&quot;y\" value=\"&lt;v&gt;\"");
        html.ShouldContain("Go &lt;now&gt;");
        html.ShouldNotContain("<script>");
    }

    private Task<ApiResponse> OneClickAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/email/unsubscribe?token={token}")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")])
        };
        return Api.SendAsync(request);
    }

    private async Task<string> IssueVerificationTokenAsync(Guid userId, DateTime? issuedAtUtc = null)
    {
        var token = OpaqueToken.Generate();
        await using var scope = Fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.UserProfiles.SingleAsync(u => u.Id == userId);
        db.EmailVerificationTokens.Add(user.RequestEmailVerification(token, OpaqueToken.Hash(token),
            issuedAtUtc ?? DateTime.UtcNow, TimeSpan.FromHours(24)));
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<string> IssueUnsubscribeTokenAsync(Guid userId)
    {
        var token = OpaqueToken.Generate();
        await using var scope = Fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.UserProfiles.SingleAsync(u => u.Id == userId);
        user.IssueUnsubscribeToken(token, OpaqueToken.Hash(token));
        await db.SaveChangesAsync();
        return token;
    }
}
