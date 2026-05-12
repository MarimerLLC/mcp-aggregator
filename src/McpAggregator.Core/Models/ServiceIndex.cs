namespace McpAggregator.Core.Models;

public class ServiceIndex
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public bool HasSkillDocument { get; set; }

    // Identity reported by the downstream MCP server's ServerInfo (compact identity only;
    // the full server-supplied instructions are surfaced via ServiceDetails).
    public string? RemoteName { get; set; }
    public string? RemoteTitle { get; set; }
    public string? RemoteVersion { get; set; }

    // Skill staleness signal. "fresh" = current tools/prompts/version match the snapshot
    // captured when the skill was authored; "stale" = drift detected; "unknown" = no
    // snapshot recorded or the server was unreachable at read time.
    public string? SkillFreshness { get; set; }
    public string? SkillRecordedVersion { get; set; }
    public DateTimeOffset? SkillRecordedAt { get; set; }

    public List<ToolSummary> Tools { get; set; } = [];
}

public class ToolSummary
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public class ServiceDetails
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public bool HasSkillDocument { get; set; }

    // Metadata supplied by the downstream MCP server itself during the initialize handshake.
    public string? RemoteName { get; set; }
    public string? RemoteTitle { get; set; }
    public string? RemoteVersion { get; set; }
    public string? RemoteInstructions { get; set; }

    public string? SkillFreshness { get; set; }
    public string? SkillRecordedVersion { get; set; }
    public DateTimeOffset? SkillRecordedAt { get; set; }

    public List<ToolDetail> Tools { get; set; } = [];
    public List<PromptDetail> Prompts { get; set; } = [];
}

public class ToolDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public object? InputSchema { get; set; }
}

public class PromptDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<PromptArgumentDetail> Arguments { get; set; } = [];
}

public class PromptArgumentDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
}
