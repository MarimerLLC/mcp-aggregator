namespace McpAggregator.Core.Models;

/// <summary>
/// What changed on a downstream since its skill document was written (issue #56). Computed by
/// <see cref="Services.SkillDriftCalculator"/> from the <see cref="SkillBaseline"/> and the live
/// catalog; reported in <c>get_service_details</c> and summarized in front of a stale document by
/// <c>get_service_skill</c>.
/// </summary>
public class SkillDrift
{
    /// <summary>The server version the skill was written for, when one was recorded.</summary>
    public string? RecordedVersion { get; set; }

    /// <summary>The version the server reports now.</summary>
    public string? CurrentVersion { get; set; }

    public DateTimeOffset? RecordedAt { get; set; }

    /// <summary>
    /// False for skills recorded before #56: no catalog snapshot exists, so only the version can be
    /// compared and every list below is empty.
    /// </summary>
    public bool BaselineAvailable { get; set; }

    public bool VersionChanged { get; set; }
    public bool InstructionsChanged { get; set; }

    public List<string> ToolsAdded { get; set; } = [];
    public List<string> ToolsRemoved { get; set; } = [];
    public List<SkillItemChange> ToolsChanged { get; set; } = [];

    public List<string> PromptsAdded { get; set; } = [];
    public List<string> PromptsRemoved { get; set; } = [];
    public List<SkillItemChange> PromptsChanged { get; set; } = [];

    /// <summary>True when the baseline exists and nothing but the version differs.</summary>
    public bool CatalogUnchanged =>
        BaselineAvailable && !InstructionsChanged
        && ToolsAdded.Count == 0 && ToolsRemoved.Count == 0 && ToolsChanged.Count == 0
        && PromptsAdded.Count == 0 && PromptsRemoved.Count == 0 && PromptsChanged.Count == 0;
}

/// <summary>
/// How one tool or prompt changed. For prompts, "parameters" are the prompt's arguments.
/// </summary>
public class SkillItemChange
{
    public required string Name { get; set; }
    public bool DescriptionChanged { get; set; }
    public List<string> ParametersAdded { get; set; } = [];
    public List<string> ParametersRemoved { get; set; } = [];

    /// <summary>Parameters whose schema (or description) or required flag changed.</summary>
    public List<string> ParametersChanged { get; set; } = [];

    /// <summary>
    /// The input schema changed outside its per-parameter <c>properties</c> and <c>required</c>
    /// (top-level keywords, <c>additionalProperties</c>, a schema that is not an object, ...).
    /// </summary>
    public bool OtherSchemaChanges { get; set; }
}
