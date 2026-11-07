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

    /// <summary>
    /// <see cref="Services.SkillFingerprint"/> of the catalog the draft was written from. The catalog
    /// can change without a version bump, so a stored draft is reused only while this still matches.
    /// </summary>
    public string? CatalogFingerprint { get; set; }

    /// <summary><see cref="Services.SkillStore.ContentHash"/> of the server instructions the draft was written from.</summary>
    public string? InstructionsSha256 { get; set; }

    /// <summary>
    /// The catalog the draft was written from, recorded as the baseline when it is published, so the
    /// published document is judged against what it was actually written for, not a catalog that
    /// changed while it waited.
    /// </summary>
    public SkillBaseline? Baseline { get; set; }

    /// <summary>What was read from the server's change sources for this draft (issue #59).</summary>
    public List<SkillSourceReport> Sources { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public string? Model { get; set; }
    public bool ValidationPassed { get; set; }

    /// <summary>
    /// The model reviewed the changes and returned the document as it is (and it passed validation).
    /// <see cref="Markdown"/> is then the current document; applying the draft records it as current
    /// without changing the text. It also marks the server state as already reviewed, so the upkeep
    /// doesn't ask the model again.
    /// </summary>
    public bool NoChanges { get; set; }

    /// <summary>
    /// Set by <c>discard_skill_draft</c> (issue #58). The file is kept as a marker so the background
    /// upkeep doesn't draft again for the same document and server state; it is hidden from
    /// <c>list_skill_drafts</c>, can't be applied, and <c>refresh_skill</c> ignores it.
    /// </summary>
    public bool Discarded { get; set; }
    public List<string> Problems { get; set; } = [];
    public List<string> Omitted { get; set; } = [];
}

/// <summary>What <c>discard_skill_draft</c> did.</summary>
public enum DraftDiscardOutcome
{
    /// <summary>The draft is now marked discarded.</summary>
    Discarded,

    /// <summary>No draft was waiting (none stored, or already discarded).</summary>
    NoDraft,

    /// <summary>A different draft was saved between reading and discarding; it was left alone.</summary>
    Replaced,
}

/// <summary>A stored draft as <c>list_skill_drafts</c> reports it (issue #58).</summary>
public class SkillDraftSummary
{
    public required string ServerName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ServerVersion { get; set; }
    public string? Model { get; set; }
    public bool ValidationPassed { get; set; }

    /// <summary>The model found nothing to change; applying records the document as current as it stands.</summary>
    public bool NoChanges { get; set; }

    public List<string> Problems { get; set; } = [];
    public List<string> Omitted { get; set; } = [];

    /// <summary>
    /// Whether the draft still fits the current document and server state (only then can it be
    /// applied); null when that could not be checked (server unreachable or unregistered).
    /// </summary>
    public bool? Current { get; set; }

    /// <summary>The draft text; only when one server was asked for.</summary>
    public string? Draft { get; set; }

    /// <summary>Unified diff from the current document to the draft; only when one server was asked for.</summary>
    public string? Diff { get; set; }
}

/// <summary>What <c>refresh_skill</c> did and found.</summary>
public class SkillRefreshResult
{
    /// <summary>
    /// <c>proposed</c>, <c>applied</c>, <c>rejected</c> (validation failed, nothing saved),
    /// <c>version_only</c>, <c>rebaselined</c>, <c>fresh</c>, <c>unknown</c>, <c>no_skill</c>,
    /// <c>no_changes</c> (the model found nothing to revise; apply records the document as current),
    /// <c>unavailable</c> (no model configured), <c>no_draft</c> / <c>obsolete</c> (<c>apply_skill_draft</c>
    /// with no draft, or one made for an earlier document or server state), <c>conflict</c> (the document was saved by someone else
    /// while the revision was prepared; nothing published) or <c>failed</c>.
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

    /// <summary>What was read from the server's change sources (issue #59): one entry per file, page, guide call or resource.</summary>
    public List<SkillSourceReport> Sources { get; set; } = [];
}
