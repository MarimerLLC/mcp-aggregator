using System.Text;
using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

/// <summary>
/// The warning <c>get_service_skill</c> puts in front of a stale skill document (issue #56), and
/// its removal on save. The block is delimited by HTML comments so <see cref="Strip"/> can find it
/// exactly; agents that fetch a skill, edit it and save it back must not store the notice.
/// </summary>
public static class SkillNotice
{
    public const string StartMarker = "<!-- aggregator:skill-notice -->";
    public const string EndMarker = "<!-- /aggregator:skill-notice -->";

    /// <summary>Change lines shown before collapsing the rest into "and N more".</summary>
    public const int MaxChangeLines = 12;

    public static string Render(SkillDrift drift)
    {
        var sb = new StringBuilder();
        sb.Append(StartMarker).Append('\n');
        sb.Append("> **This skill document may be out of date** (the aggregator's freshness check reports it stale).\n");

        var written = drift.RecordedVersion is { } rv ? $"version {rv}" : "an earlier catalog";
        var when = drift.RecordedAt is { } at ? $" ({at:yyyy-MM-dd})" : "";
        sb.Append("> It was written for ").Append(written).Append(when);
        if (drift.VersionChanged)
            sb.Append("; the server now reports ").Append(drift.CurrentVersion ?? "no version");
        sb.Append(".\n");

        if (!drift.BaselineAvailable)
        {
            sb.Append("> What changed was not recorded (this document predates catalog baselines); ")
              .Append("compare it with the live schemas.\n");
        }
        else if (drift.CatalogUnchanged)
        {
            sb.Append("> The tools, prompts and server instructions are unchanged; only the version differs.\n");
        }
        else
        {
            var lines = ChangeLines(drift).ToList();
            sb.Append("> Changes since then:\n");
            foreach (var line in lines.Take(MaxChangeLines))
                sb.Append("> - ").Append(line).Append('\n');
            if (lines.Count > MaxChangeLines)
                sb.Append("> - and ").Append(lines.Count - MaxChangeLines).Append(" more (see `skillDrift` in `get_service_details`)\n");
        }

        sb.Append("> Where this document and the live tool schemas disagree, trust the schemas (`get_service_details`).\n");
        sb.Append(EndMarker).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Removes a notice block at the start of <paramref name="markdown"/> (after leading
    /// whitespace), plus the blank lines that followed it. Anything else is returned unchanged.
    /// </summary>
    public static string Strip(string markdown)
    {
        var trimmed = markdown.TrimStart();
        if (!trimmed.StartsWith(StartMarker, StringComparison.Ordinal))
            return markdown;

        var end = trimmed.IndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0)
            return markdown;

        return trimmed[(end + EndMarker.Length)..].TrimStart('\r', '\n');
    }

    private static IEnumerable<string> ChangeLines(SkillDrift drift)
    {
        if (drift.InstructionsChanged)
            yield return "the server's instructions changed";
        foreach (var name in drift.ToolsAdded)
            yield return $"new tool `{name}`";
        foreach (var name in drift.ToolsRemoved)
            yield return $"tool `{name}` was removed";
        foreach (var change in drift.ToolsChanged)
            yield return $"tool `{change.Name}`: {Describe(change, "parameter")}";
        foreach (var name in drift.PromptsAdded)
            yield return $"new prompt `{name}`";
        foreach (var name in drift.PromptsRemoved)
            yield return $"prompt `{name}` was removed";
        foreach (var change in drift.PromptsChanged)
            yield return $"prompt `{change.Name}`: {Describe(change, "argument")}";
    }

    private static string Describe(SkillItemChange change, string noun)
    {
        var parts = new List<string>();
        if (change.ParametersAdded.Count > 0)
            parts.Add($"new {Plural(noun, change.ParametersAdded.Count)} {Names(change.ParametersAdded)}");
        if (change.ParametersRemoved.Count > 0)
            parts.Add($"removed {Plural(noun, change.ParametersRemoved.Count)} {Names(change.ParametersRemoved)}");
        if (change.ParametersChanged.Count > 0)
            parts.Add($"changed {Plural(noun, change.ParametersChanged.Count)} {Names(change.ParametersChanged)}");
        if (change.OtherSchemaChanges)
            parts.Add("input schema changed");
        if (change.DescriptionChanged)
            parts.Add("description changed");
        return string.Join("; ", parts);
    }

    private static string Plural(string noun, int count) => count == 1 ? noun : noun + "s";

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"`{n}`"));
}
