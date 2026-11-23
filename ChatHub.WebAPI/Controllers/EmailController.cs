using ChatHub.Application.Common.Emails;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.WebAPI.Extensions;
using ChatHub.WebAPI.Pages;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ChatHub.WebAPI.Controllers;

[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.EmailLink)]
public sealed class EmailController(ISender sender) : ApiController(sender)
{
    public const string OneClickField = "List-Unsubscribe";
    public const string OneClickValue = "One-Click";

    [HttpGet(EmailLinks.UnsubscribeRoute)]
    [Produces("text/html")]
    public async Task<ContentResult> UnsubscribePage([FromQuery] string? token, CancellationToken cancellationToken)
    {
        if (!IsPlausible(token)) return InvalidUnsubscribePage();

        var status = await Sender.Send(new GetUnsubscribeStatusQuery(token!), cancellationToken);
        if (status.IsFailure) return InvalidUnsubscribePage();
        if (status.Value.AlreadyUnsubscribed)
            return HtmlPage.Create(Response, StatusCodes.Status200OK, "Already unsubscribed",
                "You no longer get email notifications about unread messages. Turn them back on in your ChatHub settings.");

        return HtmlPage.Create(Response, StatusCodes.Status200OK, "Unsubscribe from ChatHub emails",
            "Stop emails about unread messages? Emails you ask for, such as address confirmations, are still sent.",
            new HtmlForm($"?token={Uri.EscapeDataString(token!)}", "Unsubscribe",
                new Dictionary<string, string> { [OneClickField] = OneClickValue }));
    }

    [HttpPost(EmailLinks.UnsubscribeRoute)]
    [Produces("text/html")]
    public async Task<ContentResult> Unsubscribe([FromQuery] string? token, CancellationToken cancellationToken)
    {
        if (!IsPlausible(token)) return InvalidUnsubscribePage();

        var result = await Sender.Send(new UnsubscribeCommand(token!), cancellationToken);
        return result.IsFailure
            ? InvalidUnsubscribePage()
            : HtmlPage.Create(Response, StatusCodes.Status200OK, "You are unsubscribed",
                "You will no longer get email notifications about unread messages. Turn them back on in your ChatHub settings.");
    }

    private static bool IsPlausible(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= UnsubscribeCommandValidator.MaxTokenLength;

    private ContentResult InvalidUnsubscribePage() =>
        HtmlPage.Create(Response, StatusCodes.Status400BadRequest, "Invalid link",
            "This unsubscribe link is not valid or no longer active. Sign in to ChatHub to change your email settings.");
}
