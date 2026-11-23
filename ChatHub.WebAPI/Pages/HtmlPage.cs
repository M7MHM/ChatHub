using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Pages;

public sealed record HtmlForm(string Action, string ButtonLabel, IReadOnlyDictionary<string, string> Fields);

public static class HtmlPage
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public static ContentResult Create(HttpResponse response, int statusCode, string title, string message, HtmlForm? form = null)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";

        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content = Render(title, message, form)
        };
    }

    public static string Render(string title, string message, HtmlForm? form = null)
    {
        var formHtml = form is null
            ? string.Empty
            : $"""
               <form method="post" action="{Encode(form.Action)}">
               {string.Concat(form.Fields.Select(f => $"""<input type="hidden" name="{Encode(f.Key)}" value="{Encode(f.Value)}">"""))}
               <button type="submit">{Encode(form.ButtonLabel)}</button>
               </form>
               """;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{Encode(title)} · ChatHub</title>
            <style>
            body {"{"} font-family: Arial, Helvetica, sans-serif; color: #1f2933; background: #f5f7fa; margin: 0; padding: 16px; {"}"}
            main {"{"} max-width: 480px; margin: 48px auto; background: #ffffff; border-radius: 8px; padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,.12); {"}"}
            h1 {"{"} font-size: 22px; margin-top: 0; {"}"}
            button {"{"} font-size: 16px; padding: 10px 16px; border: 0; border-radius: 6px; background: #3e7bfa; color: #ffffff; cursor: pointer; {"}"}
            </style>
            </head>
            <body>
            <main>
            <h1>{Encode(title)}</h1>
            <p>{Encode(message)}</p>
            {formHtml}
            </main>
            </body>
            </html>
            """;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
