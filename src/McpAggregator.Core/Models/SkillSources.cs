using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpAggregator.Core.Models;

/// <summary>
/// Optional material that tells the skill-revision model what changed on a server beyond its catalog
/// (issue #59). A catalog diff only shows schema and description changes; release notes and the
/// server's own documentation carry the behavior changes. Set with <c>set_skill_sources</c>.
/// </summary>
public class SkillSources
{
    /// <summary>
    /// Release notes or changelogs: an <c>https://</c> URL, or a GitHub location
    /// <c>owner/repo[@ref]:path</c> naming a file or a folder. A folder's files whose names start with a
    /// <c>yyyy-MM-dd</c> date are read newest first, from the skill's baseline date on.
    /// </summary>
    public string? ChangeNotes { get; set; }

    /// <summary>A downstream tool that returns the server's own documentation (e.g. Adjutant's <c>get_guide</c>).</summary>
    public string? GuideTool { get; set; }

    /// <summary>
    /// Argument objects for <see cref="GuideTool"/>, one call each (e.g. <c>{"name":"calendar"}</c>).
    /// Empty or null: one call with no arguments.
    /// </summary>
    public List<JsonElement>? GuideArguments { get; set; }

    /// <summary>Downstream resource URIs (the downstream's own form) that hold documentation.</summary>
    public List<string>? GuideResources { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ChangeNotes)
        && string.IsNullOrWhiteSpace(GuideTool)
        && (GuideResources is null || GuideResources.Count == 0);
}

/// <summary>What reading one change source produced, reported with drafts and by <c>set_skill_sources</c>.</summary>
public class SkillSourceReport
{
    /// <summary><c>change_notes</c>, <c>guide_tool</c> or <c>guide_resource</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The URL, file path, tool call or resource URI that was read.</summary>
    public required string Source { get; set; }

    /// <summary>Characters passed to the model.</summary>
    public int Chars { get; set; }

    /// <summary>True when the text was cut to the size limits.</summary>
    public bool Truncated { get; set; }

    /// <summary>Why nothing (or nothing more) was read, when something went wrong.</summary>
    public string? Error { get; set; }
}

/// <summary>Everything read from a server's change sources for one revision.</summary>
public class SkillSourceMaterial
{
    public static readonly SkillSourceMaterial None = new();

    /// <summary>Release notes and changelogs: untrusted text from outside the server.</summary>
    public string ChangeNotes { get; init; } = string.Empty;

    /// <summary>The server's own documentation (guide tool output and resources).</summary>
    public string Guides { get; init; } = string.Empty;

    public List<SkillSourceReport> Reports { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => ChangeNotes.Length == 0 && Guides.Length == 0;
}
