using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Derives the URI under which the aggregator exposes a downstream resource (issue #45):
/// <c>mcp-aggregator://{server}/{original-uri}</c>, with the downstream URI carried verbatim. The
/// server name becomes the authority and the original URI the path (query and fragment survive
/// <see cref="System.Uri"/> parsing untouched), so two servers exposing the same URI produce distinct
/// aggregator URIs, and reversing the rewrite is "strip the prefix". Templates keep their
/// <c>{…}</c> expressions: <c>mcp-aggregator://probe/file:///docs/{path}</c>.
/// <para>
/// The scheme is a fixed constant rather than <c>AggregatorOptions.SelfName</c> because URI scheme
/// characters exclude <c>_</c>, which a self name may contain.
/// </para>
/// </summary>
public static class ResourceUriNaming
{
    public const string Scheme = "mcp-aggregator";

    private const string SchemeSeparator = "://";

    /// <summary>The prefix every resource of <paramref name="serverName"/> carries: <c>mcp-aggregator://{server}/</c>.</summary>
    public static string Prefix(string serverName) => Scheme + SchemeSeparator + serverName + "/";

    /// <summary>Rewrites a downstream URI or URI template into its aggregator form.</summary>
    public static string For(string serverName, string downstreamUri) => Prefix(serverName) + downstreamUri;

    /// <summary>
    /// Splits an aggregator URI into the server name and the downstream URI. False when the value
    /// does not start with <c>mcp-aggregator://</c>, has no server name, or nothing follows the
    /// server name's <c>/</c>.
    /// </summary>
    public static bool TryParse(string? aggregatorUri, out string serverName, out string downstreamUri)
    {
        serverName = string.Empty;
        downstreamUri = string.Empty;

        if (string.IsNullOrEmpty(aggregatorUri))
            return false;

        var head = Scheme + SchemeSeparator;
        if (!aggregatorUri.StartsWith(head, StringComparison.OrdinalIgnoreCase))
            return false;

        var slash = aggregatorUri.IndexOf('/', head.Length);
        if (slash <= head.Length || slash + 1 >= aggregatorUri.Length)
            return false;

        serverName = aggregatorUri[head.Length..slash];
        downstreamUri = aggregatorUri[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// True when <paramref name="aggregatorUri"/> is an aggregator URI that belongs to
    /// <paramref name="serverName"/> (compared case-insensitively, like registry lookups).
    /// </summary>
    public static bool IsFor(string? aggregatorUri, string serverName, out string downstreamUri)
    {
        if (TryParse(aggregatorUri, out var server, out downstreamUri)
            && string.Equals(server, serverName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        downstreamUri = string.Empty;
        return false;
    }

    /// <summary>
    /// Rewrites the <see cref="ResourceContents.Uri"/> of every content block to aggregator form.
    /// A block that already carries this server's prefix is left alone; anything else — including a
    /// canonicalized or redirected URI the downstream chose to report — is prefixed.
    /// </summary>
    public static ReadResourceResult RewriteContents(string serverName, ReadResourceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var content in result.Contents)
        {
            if (content.Uri is { Length: > 0 } uri && !IsFor(uri, serverName, out _))
                content.Uri = For(serverName, uri);
        }

        return result;
    }

    /// <summary>
    /// Builds an anchored regular expression that approximates RFC 6570 matching for one
    /// downstream URI template. Literals are matched exactly; <c>{var}</c> / <c>{a,b}</c> match one
    /// path-ish segment, <c>{+var}</c> anything, <c>{#var}</c> an optional fragment,
    /// <c>{/var}</c> zero or more path segments, <c>{?var}</c> / <c>{&amp;var}</c> an optional query,
    /// <c>{.var}</c> an optional dotted label and <c>{;var}</c> optional path parameters. The SDK's own
    /// matcher is internal; this one only has to decide which same-server wrapper forwards the read,
    /// and the downstream stays authoritative.
    /// </summary>
    public static Regex BuildTemplateMatcher(string uriTemplate)
    {
        ArgumentNullException.ThrowIfNull(uriTemplate);

        var pattern = new StringBuilder("^");
        var i = 0;
        while (i < uriTemplate.Length)
        {
            var open = uriTemplate.IndexOf('{', i);
            if (open < 0)
            {
                pattern.Append(Regex.Escape(uriTemplate[i..]));
                break;
            }

            var close = uriTemplate.IndexOf('}', open + 1);
            if (close < 0)
            {
                // Unbalanced brace: treat the rest as literal text.
                pattern.Append(Regex.Escape(uriTemplate[i..]));
                break;
            }

            pattern.Append(Regex.Escape(uriTemplate[i..open]));

            var expression = uriTemplate[(open + 1)..close];
            var op = expression.Length > 0 ? expression[0] : '\0';
            pattern.Append(op switch
            {
                '+' => ".+",
                '#' => "(?:#.*)?",
                '/' => "(?:/[^/?#]+)*",
                '?' or '&' => "(?:[?&][^#]*)?",
                '.' => @"(?:\.[^/?#]+)?",
                ';' => "(?:;[^/?#]*)?",
                _ => "[^/?#]+",
            });

            i = close + 1;
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
