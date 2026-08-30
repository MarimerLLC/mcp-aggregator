using System.Text.Json.Serialization;

namespace McpAggregator.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TransportType>))]
public enum TransportType
{
    Stdio,
    Http
}

public class TransportConfig
{
    public const string RedactedValue = "***";

    public TransportType Type { get; set; }

    // Stdio transport
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }
    public Dictionary<string, string>? Environment { get; set; }

    // HTTP transport
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public TimeSpan? ConnectionTimeout { get; set; }

    /// <summary>
    /// Returns a copy with every header and environment variable value masked.
    /// Keys are preserved so callers can see which secrets are configured.
    /// </summary>
    public TransportConfig ToRedacted() => new()
    {
        Type = Type,
        Command = Command,
        Arguments = Arguments,
        Environment = Redact(Environment),
        Url = Url,
        Headers = Redact(Headers),
        ConnectionTimeout = ConnectionTimeout
    };

    private static Dictionary<string, string>? Redact(Dictionary<string, string>? values)
        => values is null ? null : values.ToDictionary(kvp => kvp.Key, _ => RedactedValue);
}
