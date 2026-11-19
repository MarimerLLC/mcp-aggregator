using System.Security.Claims;

namespace McpAggregator.Core.Security;

/// <summary>
/// Claims the hosts attach to the caller's <see cref="ClaimsPrincipal"/>. The MCP SDK carries the
/// HTTP request's user into every request handler and filter as <c>RequestContext.User</c>, so Core
/// can make per-caller decisions without knowing anything about ports or tokens.
/// </summary>
public static class AggregatorClaims
{
    /// <summary>Which endpoint authenticated the caller.</summary>
    public const string Endpoint = "mcp-aggregator/endpoint";

    /// <summary>
    /// Value of <see cref="Endpoint"/> for a caller authenticated on the public (internet-facing)
    /// endpoint. Such callers never get the administrative tools: <c>register_server</c> can launch a
    /// stdio process inside the aggregator, so admin stays on the private endpoint.
    /// </summary>
    public const string PublicEndpoint = "public";

    /// <summary>How the caller authenticated: <c>oauth</c> or <c>pat</c>.</summary>
    public const string AuthMethod = "mcp-aggregator/auth_method";

    /// <summary>True when the caller came in through the public endpoint.</summary>
    public static bool IsPublicCaller(ClaimsPrincipal? user)
        => user?.HasClaim(Endpoint, PublicEndpoint) == true;
}
