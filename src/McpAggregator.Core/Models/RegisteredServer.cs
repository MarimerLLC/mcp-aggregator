namespace McpAggregator.Core.Models;

public class RegisteredServer
{
    /// <summary>
    /// Immutable identity assigned at registration (12 lowercase hex chars) and persisted in the
    /// registry. Never user-editable and never derived from <see cref="Name"/>, so a consumer that
    /// stores it alongside a wrapper tool name can detect an unregister-and-re-register rename
    /// (issue #39). Nullable so registry files written before the field existed still load; the
    /// registry backfills it on first load.
    /// </summary>
    public string? Id { get; set; }

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

    // Snapshot of the downstream's identity and surface area at the moment the
    // skill document was last authored, used to detect drift between the skill
    // and the actual server. Null when no skill has been recorded or when the
    // server was unreachable at update time. The fingerprint is a full SHA-256
    // (64 lowercase hex) over tool names, descriptions and input schemas plus
    // prompt names, descriptions and arguments (SkillFingerprint); a 16-hex value
    // is a pre-#41 names-only fingerprint and is still matched with that algorithm.
    public string? SkillRecordedVersion { get; set; }
    public string? SkillRecordedFingerprint { get; set; }
    public DateTimeOffset? SkillRecordedAt { get; set; }
}
