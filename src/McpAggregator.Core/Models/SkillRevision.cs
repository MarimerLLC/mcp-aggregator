namespace McpAggregator.Core.Models;

/// <summary>A replaced version of a skill document, kept for <c>restore_skill</c> (issue #57).</summary>
public class SkillHistoryEntry
{
    /// <summary>The id <c>restore_skill</c> takes: the UTC time the version was replaced.</summary>
    public required string Version { get; set; }
    public DateTimeOffset? ReplacedAt { get; set; }
    public long Bytes { get; set; }

    /// <summary>Whether the catalog the version was written against was kept with it.</summary>
    public bool HasBaseline { get; set; }

    /// <summary>The server version the document was written for, when its baseline was kept.</summary>
    public string? RecordedVersion { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
}

/// <summary>
/// The pending proposal from <c>refresh_skill</c> (issue #57), stored so that a later
/// <c>apply: true</c> publishes exactly the text that was reviewed rather than asking the model again.
/// It applies only while the document it was based on is still the current one.
/// </summary>
public class SkillDraft
{
    public required string Markdown { get; set; }

    /// <summary>SHA-256 of the document the draft revises; a different current document makes the draft obsolete.</summary>
    public required string BasedOnSha256 { get; set; }

    /// <summary>The server version the draft was written for.</summary>
    public string? ServerVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string? Model { get; set; }
    public bool ValidationPassed { get; set; }
    public List<string> Problems { get; set; } = [];
    public List<string> Omitted { get; set; } = [];
}

/// <summary>What <c>refresh_skill</c> did and found.</summary>
public class SkillRefreshResult
{
    /// <summary>
    /// <c>proposed</c>, <c>applied</c>, <c>rejected</c> (validation failed, nothing saved),
    /// <c>version_only</c>, <c>rebaselined</c>, <c>fresh</c>, <c>unknown</c>, <c>no_skill</c>,
    /// <c>unavailable</c> (no model configured) or <c>failed</c>.
    /// </summary>
    public required string Status { get; set; }

    public required string Message { get; set; }

    public SkillDrift? Drift { get; set; }

    /// <summary>True when an earlier stored draft was applied without calling the model again.</summary>
    public bool FromStoredDraft { get; set; }

    public bool? ValidationPassed { get; set; }
    public List<string> Problems { get; set; } = [];

    /// <summary>Added tools or prompts the model deliberately left out, with its reasons.</summary>
    public List<string> Omitted { get; set; } = [];

    /// <summary>Unified diff from the current document to the draft.</summary>
    public string? Diff { get; set; }

    /// <summary>The full proposed document.</summary>
    public string? Draft { get; set; }
}
