using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>A dynamically registered OAuth client (RFC 7591). Carried inside its own client_id.</summary>
public sealed record ClientRegistration(string[] RedirectUris, string? ClientName, long IssuedAt);

/// <summary>An OAuth access or refresh token's contents.</summary>
public sealed record OAuthGrant(string Subject, string ClientId, string Scope, string Audience, int Generation, long ExpiresAt);

/// <summary>
/// Issues and validates everything the public endpoint hands out, without a token database: client
/// registrations, access tokens and refresh tokens are ASP.NET Data Protection payloads (authenticated
/// encryption, keys persisted under the data directory), each kind under its own purpose so one kind
/// can never be replayed as another. Personal access tokens are random strings compared by SHA-256
/// against configuration. Revocation is by <see cref="PublicEndpointOptions.TokenGeneration"/> and by
/// removing the account from <see cref="PublicEndpointOptions.AllowedEmails"/>.
/// </summary>
public sealed class TokenService
{
    public const string ClientIdPrefix = "mcpagg_client_";
    public const string AccessTokenPrefix = "mcpagg_at_";
    public const string RefreshTokenPrefix = "mcpagg_rt_";
    public const string PersonalAccessTokenPrefix = "mcpagg_pat_";
    public const string Scope = "mcp";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _clients;
    private readonly IDataProtector _accessTokens;
    private readonly IDataProtector _refreshTokens;
    private readonly IOptionsMonitor<PublicEndpointOptions> _options;
    private readonly TimeProvider _time;

    public TokenService(IDataProtectionProvider dataProtection, IOptionsMonitor<PublicEndpointOptions> options, TimeProvider time)
    {
        var root = dataProtection.CreateProtector("McpAggregator.PublicEndpoint.v1");
        _clients = root.CreateProtector("client");
        _accessTokens = root.CreateProtector("access_token");
        _refreshTokens = root.CreateProtector("refresh_token");
        _options = options;
        _time = time;
    }

    private PublicEndpointOptions Options => _options.CurrentValue;

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();

    // ---- Clients -------------------------------------------------------------------------------

    public string CreateClientId(ClientRegistration registration)
        => ClientIdPrefix + Protect(_clients, registration);

    public bool TryReadClient(string? clientId, out ClientRegistration registration)
        => TryUnprotect(_clients, ClientIdPrefix, clientId, out registration!);

    /// <summary>Whether a redirect URI may be registered by a client.</summary>
    public bool IsAllowedRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || uri.Fragment.Length > 0)
            return false;

        if (Options.AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return true;

        return Options.AllowLoopbackRedirects
               && uri.Scheme == Uri.UriSchemeHttp
               && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]")
               && string.IsNullOrEmpty(uri.UserInfo);
    }

    // ---- OAuth tokens --------------------------------------------------------------------------

    public (string AccessToken, string RefreshToken, int ExpiresIn) IssueTokens(string subject, string clientId, string scope)
    {
        var options = Options;
        var audience = options.BaseUrlTrimmed;
        var access = new OAuthGrant(subject, clientId, scope, audience, options.TokenGeneration,
            Now + (long)options.AccessTokenLifetime.TotalSeconds);
        var refresh = access with { ExpiresAt = Now + (long)options.RefreshTokenLifetime.TotalSeconds };

        return (AccessTokenPrefix + Protect(_accessTokens, access),
                RefreshTokenPrefix + Protect(_refreshTokens, refresh),
                (int)options.AccessTokenLifetime.TotalSeconds);
    }

    public bool TryValidateAccessToken(string? token, out OAuthGrant grant)
        => TryUnprotect(_accessTokens, AccessTokenPrefix, token, out grant!) && IsCurrent(grant);

    public bool TryValidateRefreshToken(string? token, out OAuthGrant grant)
        => TryUnprotect(_refreshTokens, RefreshTokenPrefix, token, out grant!) && IsCurrent(grant);

    private bool IsCurrent(OAuthGrant grant)
    {
        var options = Options;
        return grant.ExpiresAt > Now
               && grant.Generation == options.TokenGeneration
               && string.Equals(grant.Audience, options.BaseUrlTrimmed, StringComparison.Ordinal)
               && IsAllowedEmail(grant.Subject);
    }

    public bool IsAllowedEmail(string? email)
        => !string.IsNullOrWhiteSpace(email)
           && Options.AllowedEmails.Any(e => string.Equals(e.Trim(), email, StringComparison.OrdinalIgnoreCase));

    // ---- Personal access tokens ----------------------------------------------------------------

    public static string NewPersonalAccessToken()
        => PersonalAccessTokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string HashPersonalAccessToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The configured entry matching <paramref name="token"/>, if any and not expired.</summary>
    public PersonalAccessTokenEntry? ValidatePersonalAccessToken(string? token)
    {
        if (token is null || !token.StartsWith(PersonalAccessTokenPrefix, StringComparison.Ordinal))
            return null;

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        PersonalAccessTokenEntry? match = null;
        foreach (var entry in Options.PersonalAccessTokens)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(entry.Sha256.Trim());
            }
            catch (FormatException)
            {
                continue;
            }

            // Compare every entry so timing does not reveal which one matched.
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
                match = entry;
        }

        if (match?.ExpiresAt is { } expires && expires <= _time.GetUtcNow())
            return null;

        return match;
    }

    // ---- PKCE ----------------------------------------------------------------------------------

    /// <summary>RFC 7636 S256: BASE64URL(SHA256(ASCII(code_verifier))) == code_challenge.</summary>
    public static bool VerifyPkce(string? codeVerifier, string codeChallenge)
    {
        if (codeVerifier is not { Length: >= 43 and <= 128 } || !codeVerifier.All(IsUnreserved))
            return false;

        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(codeChallenge));
    }

    private static bool IsUnreserved(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~';

    // ---- Helpers -------------------------------------------------------------------------------

    private static string Protect<T>(IDataProtector protector, T payload)
        => Base64Url.EncodeToString(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload, Json)));

    private static bool TryUnprotect<T>(IDataProtector protector, string prefix, string? value, out T? payload)
    {
        payload = default;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var bytes = protector.Unprotect(Base64Url.DecodeFromChars(value.AsSpan(prefix.Length)));
            payload = JsonSerializer.Deserialize<T>(bytes, Json);
            return payload is not null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
    }
}
