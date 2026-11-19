using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>
/// Splits the two listeners by the TCP port the request arrived on (not by any header a client
/// could forge).
/// <list type="bullet">
/// <item>Public listener: requests are rewritten to <see cref="PublicEndpointOptions.BaseUrl"/>'s
/// scheme and host; only the OAuth paths and the MCP endpoint (<c>/</c>) exist, and the MCP endpoint
/// requires a bearer token. The REST API, OpenAPI, Scalar and health check answer 404.</item>
/// <item>Private listener: unchanged, except that the OAuth paths answer 404 there, so the
/// authorization server lives only at the public issuer URL.</item>
/// </list>
/// </summary>
public sealed class PublicEndpointMiddleware(RequestDelegate next, IOptions<PublicEndpointOptions> options)
{
    // Captured once: Kestrel binds the listener at startup, so a reloaded ListenUrl must never make the
    // still-bound public port look private (unauthenticated, admin API) — and nothing is parsed per request.
    private readonly int _publicPort = options.Value.ListenPort;
    private readonly Uri _baseUri = new(options.Value.BaseUrlTrimmed);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (context.Connection.LocalPort != _publicPort)
        {
            if (OAuthEndpoints.IsOAuthPath(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
            return;
        }

        // The TLS-terminating proxy (Tailscale Funnel) forwards plain HTTP; make the request look
        // like what the client actually used so redirect URIs and Secure cookies come out right.
        context.Request.Scheme = _baseUri.Scheme;
        context.Request.Host = new HostString(_baseUri.Authority);

        if (OAuthEndpoints.IsOAuthPath(path))
        {
            await next(context);
            return;
        }

        if (path == "/" || !path.HasValue)
        {
            var result = await context.AuthenticateAsync(PublicBearerHandler.SchemeName);
            if (!result.Succeeded)
            {
                await context.ChallengeAsync(PublicBearerHandler.SchemeName);
                return;
            }

            context.User = result.Principal;
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
