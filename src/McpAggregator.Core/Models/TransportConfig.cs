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
    public TransportType Type { get; set; }

    // Stdio transport
    public string? Command { get; set; }
    public string[]? Arguments { get; set; }
    public Dictionary<string, string>? Environment { get; set; }

    // HTTP transport
    public string? Url { get; set; }
}
