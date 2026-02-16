namespace McpAggregator.Core.Configuration;

public class AiOptions
{
    public const string SectionName = "McpAggregator:AI";

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4.1";
    public string ApiKey { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
