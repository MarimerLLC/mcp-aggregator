using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>The two HTML pages the authorization server shows: consent and errors.</summary>
internal static class ConsentPage
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public static IResult Consent(
        OAuthEndpoints.AuthorizeRequest request,
        ClientRegistration client,
        string redirectUri,
        string email,
        AntiforgeryTokenSet antiforgery)
    {
        var redirectHost = new Uri(redirectUri).GetLeftPart(UriPartial.Authority);
        var body = new StringBuilder();
        body.Append("<h1>Connect to MCP Aggregator?</h1>");
        body.Append("<p><strong>").Append(Html.Encode(client.ClientName ?? "An unnamed application")).Append("</strong> wants to use this aggregator's tools on your behalf.</p>");
        body.Append("<dl><dt>Signed in as</dt><dd>").Append(Html.Encode(email)).Append("</dd>");
        body.Append("<dt>Access will be sent to</dt><dd><code>").Append(Html.Encode(redirectHost)).Append("</code></dd></dl>");
        body.Append("<p class=\"note\">Only approve if you just started connecting this application. Administrative tools are never available through this endpoint.</p>");
        body.Append("<form method=\"post\" action=\"").Append(OAuthEndpoints.AuthorizePath).Append("\">");
        foreach (var (name, value) in request.Fields())
        {
            if (value is not null)
                body.Append("<input type=\"hidden\" name=\"").Append(name).Append("\" value=\"").Append(Html.Encode(value)).Append("\">");
        }
        body.Append("<input type=\"hidden\" name=\"").Append(Html.Encode(antiforgery.FormFieldName))
            .Append("\" value=\"").Append(Html.Encode(antiforgery.RequestToken ?? "")).Append("\">");
        body.Append("<button type=\"submit\" name=\"decision\" value=\"approve\">Approve</button> ");
        body.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"secondary\">Deny</button>");
        body.Append("</form>");

        return new PageResult("Connect to MCP Aggregator", body.ToString(), StatusCodes.Status200OK);
    }

    public static IResult Error(string title, string message, int statusCode = StatusCodes.Status400BadRequest)
        => new PageResult(title, $"<h1>{Html.Encode(title)}</h1><p>{Html.Encode(message)}</p>", statusCode);

    private sealed class PageResult(string title, string body, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            await context.Response.WriteAsync($$"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{Html.Encode(title)}}</title>
                <style>
                  :root { color-scheme: light dark; --fg:#1b1b1f; --bg:#fafafa; --muted:#5d5d66; --accent:#2f5bd3; }
                  @media (prefers-color-scheme: dark) { :root { --fg:#ececf1; --bg:#16161a; --muted:#a0a0ab; --accent:#7d9bff; } }
                  body { font: 16px/1.5 system-ui, sans-serif; color: var(--fg); background: var(--bg); margin: 0; padding: 16px; }
                  main { max-width: 32rem; margin: 10vh auto; }
                  h1 { font-size: 1.4rem; } dt { color: var(--muted); font-size: .9rem; } dd { margin: 0 0 .75rem; }
                  .note { color: var(--muted); font-size: .9rem; }
                  button { font: inherit; padding: .5rem 1.25rem; border-radius: 6px; border: 1px solid var(--accent); background: var(--accent); color: #fff; cursor: pointer; }
                  button.secondary { background: transparent; color: var(--fg); border-color: var(--muted); }
                </style></head>
                <body><main>{{body}}</main></body></html>
                """);
        }
    }
}
