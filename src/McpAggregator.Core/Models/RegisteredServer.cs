namespace McpAggregator.Core.Models;

public class RegisteredServer
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public required TransportConfig Transport { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public bool HasSkillDocument { get; set; }
    public string? AiSummary { get; set; }

    // Metadata captured from the downstream MCP server on connection.
    public string? RemoteName { get; set; }
    public string? RemoteTitle { get; set; }
    public string? RemoteVersion { get; set; }
    public string? RemoteInstructions { get; set; }
}
