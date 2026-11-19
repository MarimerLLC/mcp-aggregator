using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace McpAggregator.HttpServer.PublicEndpoint;

public sealed record AuthorizationCode(
    string ClientId,
    string RedirectUri,
    bool RedirectUriSent,
    string CodeChallenge,
    string Subject,
    string Scope,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Single-use authorization codes, held in memory for a couple of minutes. The deployment runs one
/// replica, and a code lost to a restart only means the client restarts the authorization.
/// </summary>
public sealed class AuthorizationCodeStore(IOptionsMonitor<PublicEndpointOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);

    /// <param name="redirectUriSent">Whether the authorization request named the redirect URI (rather
    /// than defaulting to the client's only one); the token request must then repeat it.</param>
    public string Issue(string clientId, string redirectUri, bool redirectUriSent, string codeChallenge, string subject, string scope)
    {
        var now = time.GetUtcNow();
        foreach (var (key, value) in _codes)
        {
            if (value.ExpiresAt <= now)
                _codes.TryRemove(key, out _);
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new AuthorizationCode(clientId, redirectUri, redirectUriSent, codeChallenge, subject, scope,
            now + options.CurrentValue.AuthorizationCodeLifetime);
        return code;
    }

    /// <summary>Removes and returns the code; a second redemption finds nothing.</summary>
    public bool TryRedeem(string? code, out AuthorizationCode grant)
    {
        if (code is not null && _codes.TryRemove(code, out var found) && found.ExpiresAt > time.GetUtcNow())
        {
            grant = found;
            return true;
        }

        grant = null!;
        return false;
    }
}
