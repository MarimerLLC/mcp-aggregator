using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>
/// A single-user OAuth 2.1 authorization server, just enough for MCP authorization: protected
/// resource metadata (RFC 9728), authorization server metadata (RFC 8414), dynamic client
/// registration (RFC 7591), the authorization code grant with mandatory PKCE S256, and refresh
/// tokens. Identity comes from Google sign-in restricted to
/// <see cref="PublicEndpointOptions.AllowedEmails"/>, followed by an explicit consent click.
/// Served only on the public listener (<see cref="PublicEndpointMiddleware"/>).
/// </summary>
public static class OAuthEndpoints
{
    public const string ProtectedResourceMetadataPath = "/.well-known/oauth-protected-resource";
    public const string AuthorizationServerMetadataPath = "/.well-known/oauth-authorization-server";
    public const string RegisterPath = "/oauth/register";
    public const string AuthorizePath = "/oauth/authorize";
    public const string TokenPath = "/oauth/token";
    public const string GoogleCallbackPath = "/oauth/google/callback";

    public const string LoginCookieScheme = "McpAggregatorLogin";
    public const string GoogleScheme = "Google";

    public static bool IsOAuthPath(PathString path)
        => path.StartsWithSegments("/oauth")
           || path.Equals(ProtectedResourceMetadataPath)
           || path.Equals(AuthorizationServerMetadataPath);

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ProtectedResourceMetadataPath, (IOptionsMonitor<PublicEndpointOptions> o) =>
        {
            var baseUrl = o.CurrentValue.BaseUrlTrimmed;
            return Results.Json(new
            {
                resource = baseUrl,
                authorization_servers = new[] { baseUrl },
                scopes_supported = new[] { TokenService.Scope },
                bearer_methods_supported = new[] { "header" },
                resource_name = "MCP Aggregator",
            });
        });

        app.MapGet(AuthorizationServerMetadataPath, (IOptionsMonitor<PublicEndpointOptions> o) =>
        {
            var baseUrl = o.CurrentValue.BaseUrlTrimmed;
            return Results.Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = baseUrl + AuthorizePath,
                token_endpoint = baseUrl + TokenPath,
                registration_endpoint = baseUrl + RegisterPath,
                scopes_supported = new[] { TokenService.Scope },
                response_types_supported = new[] { "code" },
                response_modes_supported = new[] { "query" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                code_challenge_methods_supported = new[] { "S256" },
                authorization_response_iss_parameter_supported = true,
            });
        });

        app.MapPost(RegisterPath, Register).DisableAntiforgery();
        app.MapGet(AuthorizePath, AuthorizeGet);
        app.MapPost(AuthorizePath, AuthorizePost).DisableAntiforgery(); // validated explicitly
        app.MapPost(TokenPath, Token).DisableAntiforgery();
    }

    // ---- Dynamic client registration (RFC 7591) -------------------------------------------------

    public sealed class RegistrationRequest
    {
        [JsonPropertyName("redirect_uris")] public string[]? RedirectUris { get; set; }
        [JsonPropertyName("client_name")] public string? ClientName { get; set; }
    }

    private static IResult Register(RegistrationRequest? request, TokenService tokens, TimeProvider time, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(OAuthEndpoints));

        if (request?.RedirectUris is not { Length: > 0 and <= 10 } uris)
            return RegistrationError("invalid_redirect_uri", "redirect_uris must list between 1 and 10 URIs.");

        var rejected = uris.Where(u => !tokens.IsAllowedRedirectUri(u)).ToList();
        if (rejected.Count > 0)
        {
            logger.LogWarning("Client registration refused redirect URIs {RedirectUris}", rejected);
            return RegistrationError("invalid_redirect_uri",
                $"Redirect URI(s) not allowed by this server: {string.Join(", ", rejected)}. " +
                "Add them to McpAggregator:PublicEndpoint:AllowedRedirectUris.");
        }

        var name = request.ClientName is { Length: > 0 } n ? n[..Math.Min(n.Length, 100)] : null;
        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();
        var clientId = tokens.CreateClientId(new ClientRegistration(uris, name, issuedAt));

        logger.LogInformation("Registered OAuth client {ClientName} with redirect URIs {RedirectUris}", name ?? "(unnamed)", uris);

        return Results.Json(new
        {
            client_id = clientId,
            client_id_issued_at = issuedAt,
            client_name = name,
            redirect_uris = uris,
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    // ---- Authorization endpoint ------------------------------------------------------------------

    /// <summary>The authorization request parameters, from the query (GET) or the consent form (POST).</summary>
    internal sealed record AuthorizeRequest(
        string? ResponseType,
        string? ClientId,
        string? RedirectUri,
        string? State,
        string? CodeChallenge,
        string? CodeChallengeMethod,
        string? Resource,
        string? Scope)
    {
        public static AuthorizeRequest From(Func<string, StringValues> get)
            => new(Single(get("response_type")), Single(get("client_id")), Single(get("redirect_uri")),
                   Single(get("state")), Single(get("code_challenge")), Single(get("code_challenge_method")),
                   Single(get("resource")), Single(get("scope")));

        private static string? Single(StringValues v) => v.Count == 1 && v[0] is { Length: > 0 } s ? s : null;

        public IEnumerable<(string Name, string? Value)> Fields()
        {
            yield return ("response_type", ResponseType);
            yield return ("client_id", ClientId);
            yield return ("redirect_uri", RedirectUri);
            yield return ("state", State);
            yield return ("code_challenge", CodeChallenge);
            yield return ("code_challenge_method", CodeChallengeMethod);
            yield return ("resource", Resource);
            yield return ("scope", Scope);
        }
    }

    /// <summary>
    /// The outcome of validating an authorization request. Until the client and redirect URI are
    /// known good, errors are shown to the user (never redirected, RFC 6749 §4.1.2.1); after that
    /// they go back to the client.
    /// </summary>
    private sealed record Validated(ClientRegistration? Client, string? RedirectUri, IResult? Error);

    private static Validated Validate(AuthorizeRequest r, TokenService tokens, PublicEndpointOptions options)
    {
        if (!tokens.TryReadClient(r.ClientId, out var client))
            return new(null, null, ConsentPage.Error("Unknown client", "This client is not registered with the aggregator. Remove the connector and add it again."));

        var redirectUri = r.RedirectUri ?? (client.RedirectUris.Length == 1 ? client.RedirectUris[0] : null);
        if (redirectUri is null || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal) || !tokens.IsAllowedRedirectUri(redirectUri))
            return new(null, null, ConsentPage.Error("Invalid redirect", "The redirect URI does not match the client's registration."));

        if (r.ResponseType != "code")
            return new(client, redirectUri, ErrorRedirect(redirectUri, r.State, options, "unsupported_response_type", "Only response_type=code is supported."));

        if (r.CodeChallenge is not { Length: >= 43 and <= 128 } || r.CodeChallengeMethod != "S256")
            return new(client, redirectUri, ErrorRedirect(redirectUri, r.State, options, "invalid_request", "PKCE with code_challenge_method=S256 is required."));

        if (r.Resource is not null && !string.Equals(r.Resource.TrimEnd('/'), options.BaseUrlTrimmed, StringComparison.OrdinalIgnoreCase))
            return new(client, redirectUri, ErrorRedirect(redirectUri, r.State, options, "invalid_target", $"This server only issues tokens for {options.BaseUrlTrimmed}."));

        return new(client, redirectUri, null);
    }

    private static async Task<IResult> AuthorizeGet(
        HttpContext context, TokenService tokens, IOptionsMonitor<PublicEndpointOptions> options, IAntiforgery antiforgery)
    {
        var request = AuthorizeRequest.From(k => context.Request.Query[k]);
        var opts = options.CurrentValue;
        var validated = Validate(request, tokens, opts);
        if (validated.Error is not null)
            return validated.Error;

        var login = await context.AuthenticateAsync(LoginCookieScheme);
        if (!login.Succeeded)
        {
            var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
            if (await schemes.GetSchemeAsync(GoogleScheme) is null)
            {
                return ConsentPage.Error("Sign-in not configured",
                    "Google sign-in is not configured on this aggregator (McpAggregator:PublicEndpoint:Google). " +
                    "Use a personal access token instead, or ask the operator to configure it.",
                    StatusCodes.Status503ServiceUnavailable);
            }

            // Off to Google; its callback signs in the login cookie and comes back here.
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString },
                [GoogleScheme]);
        }

        var email = AllowedEmail(login.Principal, tokens);
        if (email is null)
        {
            await context.SignOutAsync(LoginCookieScheme);
            return ConsentPage.Error("Account not allowed",
                $"{login.Principal.FindFirstValue(ClaimTypes.Email) ?? "This account"} is not allowed to use this aggregator. " +
                "Reload the page to sign in with a different Google account.", StatusCodes.Status403Forbidden);
        }

        // Bind the antiforgery token to the signed-in user.
        context.User = login.Principal;
        var antiforgeryTokens = antiforgery.GetAndStoreTokens(context);

        return ConsentPage.Consent(request, validated.Client!, validated.RedirectUri!, email, antiforgeryTokens);
    }

    private static async Task<IResult> AuthorizePost(
        HttpContext context, TokenService tokens, AuthorizationCodeStore codes,
        IOptionsMonitor<PublicEndpointOptions> options, IAntiforgery antiforgery, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(OAuthEndpoints));
        var form = await context.Request.ReadFormAsync();
        var request = AuthorizeRequest.From(k => form[k]);
        var opts = options.CurrentValue;
        var validated = Validate(request, tokens, opts);
        if (validated.Error is not null)
            return validated.Error;

        var login = await context.AuthenticateAsync(LoginCookieScheme);
        var email = login.Succeeded ? AllowedEmail(login.Principal, tokens) : null;
        if (email is null)
            return ConsentPage.Error("Sign-in expired", "Start again from the application that sent you here.", StatusCodes.Status403Forbidden);

        context.User = login.Principal!;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return ConsentPage.Error("Request expired", "The consent form expired or was not submitted from this page. Start again.", StatusCodes.Status400BadRequest);
        }

        // One consent per sign-in: the next authorization asks Google again.
        await context.SignOutAsync(LoginCookieScheme);

        var redirectUri = validated.RedirectUri!;
        if (form["decision"] != "approve")
        {
            logger.LogInformation("{Email} denied OAuth client {ClientName}", email, validated.Client!.ClientName);
            return ErrorRedirect(redirectUri, request.State, opts, "access_denied", "The user denied the request.");
        }

        var code = codes.Issue(request.ClientId!, redirectUri, request.CodeChallenge!, email, TokenService.Scope);
        logger.LogInformation("{Email} authorized OAuth client {ClientName} ({RedirectUri})", email, validated.Client!.ClientName, redirectUri);

        return SeeOther(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = request.State,
            ["iss"] = opts.BaseUrlTrimmed,
        }.Where(kv => kv.Value is not null)));
    }

    private static string? AllowedEmail(ClaimsPrincipal? principal, TokenService tokens)
    {
        var email = principal?.FindFirstValue(ClaimTypes.Email);
        var verified = principal?.FindFirstValue("email_verified");
        return string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase) && tokens.IsAllowedEmail(email)
            ? email
            : null;
    }

    private static IResult ErrorRedirect(string redirectUri, string? state, PublicEndpointOptions options, string error, string description)
        => SeeOther(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = state,
            ["iss"] = options.BaseUrlTrimmed,
        }.Where(kv => kv.Value is not null)));

    private static IResult SeeOther(string location) => new SeeOtherResult(location);

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }

    // ---- Token endpoint ------------------------------------------------------------------------

    private static async Task<IResult> Token(
        HttpContext context, TokenService tokens, AuthorizationCodeStore codes, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(OAuthEndpoints));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (!context.Request.HasFormContentType)
            return TokenError("invalid_request", "Expected application/x-www-form-urlencoded.");

        var form = await context.Request.ReadFormAsync();
        string? Field(string name) => form[name] is { Count: 1 } v && v[0] is { Length: > 0 } s ? s : null;

        var clientId = Field("client_id");
        if (!tokens.TryReadClient(clientId, out _))
            return TokenError("invalid_client", "Unknown client_id.", StatusCodes.Status401Unauthorized);

        switch (Field("grant_type"))
        {
            case "authorization_code":
            {
                if (!codes.TryRedeem(Field("code"), out var grant)
                    || !string.Equals(grant.ClientId, clientId, StringComparison.Ordinal)
                    || !string.Equals(grant.RedirectUri, Field("redirect_uri") ?? grant.RedirectUri, StringComparison.Ordinal)
                    || !TokenService.VerifyPkce(Field("code_verifier"), grant.CodeChallenge)
                    || !tokens.IsAllowedEmail(grant.Subject))
                {
                    logger.LogWarning("Rejected authorization_code grant for client {ClientId}", Truncate(clientId));
                    return TokenError("invalid_grant", "The authorization code is invalid, expired, already used, or the code_verifier does not match.");
                }

                return TokenResponse(tokens.IssueTokens(grant.Subject, clientId!, grant.Scope));
            }

            case "refresh_token":
            {
                if (!tokens.TryValidateRefreshToken(Field("refresh_token"), out var grant)
                    || !string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
                {
                    logger.LogInformation("Rejected refresh_token grant for client {ClientId}", Truncate(clientId));
                    return TokenError("invalid_grant", "The refresh token is invalid, expired or revoked.");
                }

                return TokenResponse(tokens.IssueTokens(grant.Subject, clientId!, grant.Scope));
            }

            default:
                return TokenError("unsupported_grant_type", "Supported: authorization_code, refresh_token.");
        }
    }

    private static IResult TokenResponse((string AccessToken, string RefreshToken, int ExpiresIn) issued)
        => Results.Json(new
        {
            access_token = issued.AccessToken,
            token_type = "Bearer",
            expires_in = issued.ExpiresIn,
            refresh_token = issued.RefreshToken,
            scope = TokenService.Scope,
        });

    private static IResult TokenError(string error, string description, int status = StatusCodes.Status400BadRequest)
        => Results.Json(new { error, error_description = description }, statusCode: status);

    private static string Truncate(string? s) => s is null ? "(none)" : s.Length <= 24 ? s : s[..24] + "…";
}
