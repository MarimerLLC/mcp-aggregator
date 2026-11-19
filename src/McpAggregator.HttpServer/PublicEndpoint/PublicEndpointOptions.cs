using Microsoft.AspNetCore.Http;

namespace McpAggregator.HttpServer.PublicEndpoint;

/// <summary>
/// The internet-facing endpoint (e.g. behind Tailscale Funnel). It is a second Kestrel listener
/// that serves only MCP and the built-in OAuth authorization server, and requires a bearer token
/// on every MCP request: an OAuth access token (claude.ai custom connectors, via Google sign-in) or
/// a personal access token (header-capable clients such as Claude Code). The private listener is
/// unchanged. Section <c>McpAggregator:PublicEndpoint</c>.
/// </summary>
public sealed class PublicEndpointOptions
{
    public const string SectionName = "McpAggregator:PublicEndpoint";

    public bool Enabled { get; set; }

    /// <summary>Kestrel binding for the public listener, e.g. <c>http://+:8081</c>.</summary>
    public string ListenUrl { get; set; } = "http://+:8081";

    /// <summary>
    /// The externally visible origin, e.g. <c>https://mcp-aggregator-public.example.ts.net</c>. It is
    /// the OAuth issuer and the protected resource identifier, and the MCP endpoint is its root.
    /// Requests on the public listener are rewritten to this scheme and host, because the TLS-terminating
    /// proxy in front of it forwards plain HTTP.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Google accounts allowed to authorize a connector. Empty means nobody can.</summary>
    public List<string> AllowedEmails { get; set; } = [];

    public GoogleLoginOptions Google { get; set; } = new();

    public List<PersonalAccessTokenEntry> PersonalAccessTokens { get; set; } = [];

    /// <summary>
    /// Redirect URIs a dynamically registered client may use, compared exactly. Defaults cover
    /// claude.ai / claude.com custom connectors.
    /// </summary>
    public List<string> AllowedRedirectUris { get; set; } =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
    ];

    /// <summary>
    /// Also accept <c>http://localhost:{port}/…</c> and <c>http://127.0.0.1:{port}/…</c> redirect URIs
    /// (RFC 8252 native clients such as Claude Code and mcp-remote).
    /// </summary>
    public bool AllowLoopbackRedirects { get; set; } = true;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Stamped into every OAuth token; tokens carrying a different value are rejected. Bump it to
    /// revoke every issued access and refresh token at once.
    /// </summary>
    public int TokenGeneration { get; set; } = 1;

    public string BaseUrlTrimmed => BaseUrl.TrimEnd('/');

    /// <summary>The port the public listener binds, used to tell its requests apart.</summary>
    public int ListenPort => BindingAddress.Parse(ListenUrl).Port;
}

public sealed class GoogleLoginOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class PersonalAccessTokenEntry
{
    /// <summary>A label for logs and the caller identity, e.g. <c>claude-code-cloud</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the token (the token itself is never stored).</summary>
    public string Sha256 { get; set; } = "";

    public DateTimeOffset? ExpiresAt { get; set; }
}
