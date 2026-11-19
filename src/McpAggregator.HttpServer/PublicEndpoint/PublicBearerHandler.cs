using System.Security.Claims;
using System.Text.Encodings.Web;
using McpAggregator.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>
/// Authenticates MCP requests on the public listener from <c>Authorization: Bearer …</c>: a personal
/// access token or an OAuth access token issued by <see cref="OAuthEndpoints"/>. The resulting
/// principal carries <see cref="AggregatorClaims.PublicEndpoint"/>, which Core uses to withhold the
/// administrative tools. The challenge is the MCP authorization one: 401 with a
/// <c>WWW-Authenticate</c> pointing at the protected resource metadata (RFC 9728), which is how
/// claude.ai discovers the authorization server.
/// </summary>
public sealed class PublicBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    TokenService tokens,
    IOptionsMonitor<PublicEndpointOptions> publicOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "McpAggregatorPublicBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = header["Bearer ".Length..].Trim();

        if (token.StartsWith(TokenService.PersonalAccessTokenPrefix, StringComparison.Ordinal))
        {
            var pat = tokens.ValidatePersonalAccessToken(token);
            if (pat is null)
                return Fail("unknown or expired personal access token");

            return Success($"pat:{pat.Name}", pat.Name, "pat", clientId: null);
        }

        if (tokens.TryValidateAccessToken(token, out var grant))
            return Success(grant.Subject, grant.Subject, "oauth", grant.ClientId);

        return Fail("invalid, expired or revoked access token");
    }

    private Task<AuthenticateResult> Fail(string reason)
    {
        Logger.LogWarning("Public endpoint rejected a bearer token: {Reason}", reason);
        Context.Items[typeof(PublicBearerHandler)] = "invalid_token";
        return Task.FromResult(AuthenticateResult.Fail(reason));
    }

    private Task<AuthenticateResult> Success(string subject, string name, string method, string? clientId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new("sub", subject),
            new(ClaimTypes.Name, name),
            new(AggregatorClaims.AuthMethod, method),
            new(AggregatorClaims.Endpoint, AggregatorClaims.PublicEndpoint),
        };
        if (clientId is not null)
            claims.Add(new Claim("client_id", clientId));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = BuildChallenge(
            publicOptions.CurrentValue,
            Context.Items[typeof(PublicBearerHandler)] as string);
        return Task.CompletedTask;
    }

    internal static string BuildChallenge(PublicEndpointOptions options, string? error)
    {
        var challenge = $"Bearer resource_metadata=\"{options.BaseUrlTrimmed}{OAuthEndpoints.ProtectedResourceMetadataPath}\", scope=\"{TokenService.Scope}\"";
        if (error is not null)
            challenge += $", error=\"{error}\"";
        return challenge;
    }
}
