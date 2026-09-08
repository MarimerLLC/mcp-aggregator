using System.Text;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Derives the name of the typed wrapper tool the aggregator exposes for a downstream tool.
/// The name is <c>{serverName}__{toolName}</c>: readable, unique across servers (two servers with
/// identical tool names produce distinct wrappers), and derived from the registered name rather than
/// the immutable <see cref="Models.RegisteredServer.Id"/> so a stale reference fails visibly after a
/// rename instead of pointing at an opaque identifier (issue #39, Q2/Q3).
/// </summary>
public static class WrapperNaming
{
    public const string Separator = "__";

    /// <summary>
    /// Length above which hosts are known to drop or reject a tool name (Claude enforces roughly
    /// <c>^[a-zA-Z0-9_-]{1,64}$</c>). Names are not truncated — truncation would silently collide —
    /// but the catalog logs a warning when a wrapper exceeds it.
    /// </summary>
    public const int HostNameLengthLimit = 64;

    public static string For(string serverName, string toolName)
        => Sanitize(serverName) + Separator + Sanitize(toolName);

    /// <summary>
    /// Splits a wrapper name on its first separator. Diagnostics only — runtime routing uses the
    /// wrapper instance's own fields, never a parse of its name.
    /// </summary>
    public static bool TryParse(string wrapperName, out string serverName, out string toolName)
    {
        serverName = string.Empty;
        toolName = string.Empty;

        if (string.IsNullOrEmpty(wrapperName))
            return false;

        var idx = wrapperName.IndexOf(Separator, StringComparison.Ordinal);
        if (idx <= 0 || idx + Separator.Length >= wrapperName.Length)
            return false;

        serverName = wrapperName[..idx];
        toolName = wrapperName[(idx + Separator.Length)..];
        return true;
    }

    /// <summary>
    /// True when <paramref name="c"/> is allowed in an MCP tool name by the SDK
    /// (<c>^[A-Za-z0-9_.-]{1,128}$</c>).
    /// </summary>
    public static bool IsAllowedChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';

    private static string Sanitize(string value)
    {
        if (value.All(IsAllowedChar))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(IsAllowedChar(c) ? c : '-');
        return sb.ToString();
    }
}
