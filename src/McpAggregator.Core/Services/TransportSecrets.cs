using System.Text.RegularExpressions;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

/// <summary>
/// Expands <c>${VAR}</c> references in transport configuration values from the process
/// environment. Resolution happens at connect time, so rotating a variable and reconnecting
/// picks up the new value without re-registering the server.
/// </summary>
public static partial class TransportSecrets
{
    [GeneratedRegex(@"\$\$\{|\$\{([^}]*)\}")]
    private static partial Regex ReferenceRegex { get; }

    /// <summary>
    /// Expands every <c>${NAME}</c> occurrence in <paramref name="value"/>. A literal
    /// <c>${</c> is written as <c>$${</c>.
    /// </summary>
    public static string Resolve(string value)
    {
        return ReferenceRegex.Replace(value, match =>
        {
            if (!match.Groups[1].Success)
                return "${"; // $${ escape

            var name = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidTransportConfigException("Empty environment variable reference '${}'.");

            return Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidTransportConfigException(
                    $"Environment variable '{name}' referenced by the transport configuration is not set.");
        });
    }

    /// <summary>
    /// Resolves the configured HTTP headers, or returns null when none are configured.
    /// </summary>
    public static Dictionary<string, string>? ResolveHeaders(TransportConfig config)
    {
        if (config.Headers is null || config.Headers.Count == 0)
            return null;

        return config.Headers.ToDictionary(kvp => kvp.Key, kvp => Resolve(kvp.Value));
    }
}
