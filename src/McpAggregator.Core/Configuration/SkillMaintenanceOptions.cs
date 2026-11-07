namespace McpAggregator.Core.Configuration;

/// <summary>
/// LLM-assisted skill revision (issue #57). The model endpoint and key are the ones in
/// <see cref="AiOptions"/>; <see cref="Model"/> only overrides the model name, because rewriting a
/// whole skill document needs more than the 300-character summaries <c>McpAggregator:AI</c> is
/// tuned for.
/// </summary>
public class SkillMaintenanceOptions
{
    public const string SectionName = "McpAggregator:SkillMaintenance";

    /// <summary>What the background upkeep does (issue #58). Default <see cref="SkillMaintenanceMode.Notify"/>.</summary>
    public SkillMaintenanceMode Mode { get; set; } = SkillMaintenanceMode.Notify;

    /// <summary>How often every skill's freshness is checked.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Wait after start-up before the first check, so downstreams and the index can warm up.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Model calls the background upkeep may make per UTC day, across all servers.</summary>
    public int MaxRevisionsPerDay { get; set; } = 10;

    /// <summary>
    /// After a revision attempt fails (model error or timeout) the server is skipped for this long,
    /// doubling with each further failure up to <see cref="MaxFailureBackoff"/>.
    /// </summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan MaxFailureBackoff { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Key of the <c>IChatClient</c> the hosts register when <see cref="Model"/> is set.</summary>
    public const string ChatClientKey = "skill-maintenance";

    /// <summary>Model for skill revision; null or empty uses <see cref="AiOptions.Model"/>.</summary>
    public string? Model { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>
    /// A draft shorter than this fraction of the current document fails validation (only checked when
    /// the current document is at least <see cref="MinLengthForRatioCheck"/> characters).
    /// </summary>
    public double MinLengthRatio { get; set; } = 0.5;

    /// <summary>A draft longer than this multiple of the current document fails validation.</summary>
    public double MaxLengthRatio { get; set; } = 2.5;

    public int MinLengthForRatioCheck { get; set; } = 400;

    /// <summary>
    /// Hosts change notes may be fetched from (exact match, https only; issue #59). GitHub locations
    /// need <c>api.github.com</c> and, for folders, <c>raw.githubusercontent.com</c>. Configured values
    /// are added to these defaults.
    /// </summary>
    public List<string> AllowedChangeNoteHosts { get; set; } = ["api.github.com", "raw.githubusercontent.com"];

    /// <summary>Optional token for GitHub change notes in private repositories (keep it in a secret).</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Characters taken from one change source (a file, a page, one guide call or resource).</summary>
    public int MaxSourceChars { get; set; } = 20_000;

    /// <summary>Characters taken from all change sources of one server together.</summary>
    public int MaxSourcesTotalChars { get; set; } = 60_000;

    /// <summary>How long reading one change source may take.</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
