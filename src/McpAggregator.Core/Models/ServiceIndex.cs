using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Models;

public class ServiceIndex
{
    /// <summary>Immutable server identity; see <see cref="RegisteredServer.Id"/>.</summary>
    public string? Id { get; set; }
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

    // Skill staleness signal, compared against the snapshot captured when the skill was
    // authored. "fresh" = the tool names, descriptions and input schemas, the prompt names,
    // descriptions and arguments, and (when one was recorded) the server's reported version
    // all match; "stale" = any of those drifted; "unknown" = no snapshot recorded, or the
    // server's tools or prompts could not be read at read time.
    public string? SkillFreshness { get; set; }
    public string? SkillRecordedVersion { get; set; }
    public DateTimeOffset? SkillRecordedAt { get; set; }

    public List<ToolSummary> Tools { get; set; } = [];
}

public class ToolSummary
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Name of the typed wrapper tool the aggregator exposes for this downstream tool
    /// (<c>{server}__{tool}</c>). Callable directly once it appears in the client's tool list.
    /// </summary>
    public string? WrapperName { get; set; }
}

public class ServiceDetails
{
    /// <summary>Immutable server identity; see <see cref="RegisteredServer.Id"/>.</summary>
    public string? Id { get; set; }
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
    public List<ResourceDetail> Resources { get; set; } = [];
}

public class ToolDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public object? InputSchema { get; set; }

    /// <summary>
    /// Name of the typed wrapper tool the aggregator exposes for this downstream tool
    /// (<c>{server}__{tool}</c>).
    /// </summary>
    public string? WrapperName { get; set; }

    /// <summary>
    /// The downstream tool exactly as it came over the wire (title, annotations, output schema,
    /// icons, meta). Kept so <see cref="Tools.DownstreamToolWrapper"/> can carry everything through
    /// unchanged; not part of the serialized consumer surface.
    /// </summary>
    [JsonIgnore]
    public Tool? Protocol { get; set; }
}

public class PromptDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<PromptArgumentDetail> Arguments { get; set; } = [];

    /// <summary>
    /// Name of the MCP prompt the aggregator exposes for this downstream prompt
    /// (<c>{server}__{prompt}</c>). Requested through <c>prompts/get</c> once it appears in the
    /// client's prompt list.
    /// </summary>
    public string? WrapperName { get; set; }

    /// <summary>
    /// The downstream prompt exactly as it came over the wire (title, arguments, icons, meta).
    /// Kept so <see cref="Tools.DownstreamPromptWrapper"/> can carry everything through unchanged;
    /// not part of the serialized consumer surface.
    /// </summary>
    [JsonIgnore]
    public Prompt? Protocol { get; set; }
}

public class PromptArgumentDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>One downstream resource or resource template as the aggregator exposes it (issue #45).</summary>
public class ResourceDetail
{
    public required string Name { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? MimeType { get; set; }
    public long? Size { get; set; }

    /// <summary>True for a resource template (<see cref="DownstreamUri"/> carries <c>{…}</c> expressions).</summary>
    public bool IsTemplate { get; set; }

    /// <summary>The downstream's own URI (or URI template), exactly as it declared it.</summary>
    public required string DownstreamUri { get; set; }

    /// <summary>
    /// The URI (or URI template) under which the aggregator exposes this resource:
    /// <c>mcp-aggregator://{server}/{downstreamUri}</c>. Read through <c>resources/read</c> once it
    /// appears in the client's resource list, or at any time by URI.
    /// </summary>
    public required string Uri { get; set; }

    /// <summary>The downstream resource exactly as it came over the wire; null for templates. Not serialized.</summary>
    [JsonIgnore]
    public Resource? Protocol { get; set; }

    /// <summary>The downstream resource template exactly as it came over the wire; null for plain resources. Not serialized.</summary>
    [JsonIgnore]
    public ResourceTemplate? ProtocolTemplate { get; set; }
}
