using System.Text.RegularExpressions;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

/// <summary>
/// Mechanical checks on an LLM-revised skill document before it can be saved (issue #57). They are
/// deliberately not model-judged: the inputs the model saw come from downstream servers and are
/// untrusted, so the gate must not be something the same text can talk its way past.
/// </summary>
public static partial class SkillDraftValidator
{
    public static List<string> Validate(
        string current,
        string draft,
        SkillDrift? drift,
        IReadOnlyCollection<string> omitted,
        string allowedSourceText,
        SkillMaintenanceOptions options)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(draft))
        {
            problems.Add("The draft is empty.");
            return problems;
        }

        if (draft.Length > SkillStore.MaxSkillSize)
            problems.Add($"The draft is {draft.Length} characters; the limit is {SkillStore.MaxSkillSize}.");

        if (current.Length >= options.MinLengthForRatioCheck)
        {
            var ratio = (double)draft.Length / current.Length;
            if (ratio < options.MinLengthRatio)
                problems.Add($"The draft is {ratio:P0} of the current document's length (minimum {options.MinLengthRatio:P0}); it may have dropped guidance.");
            if (ratio > options.MaxLengthRatio)
                problems.Add($"The draft is {ratio:P0} of the current document's length (maximum {options.MaxLengthRatio:P0}).");
        }

        if (drift is { BaselineAvailable: true })
        {
            foreach (var name in drift.ToolsRemoved)
                if (Mentions(draft, name))
                    problems.Add($"The draft still mentions removed tool `{name}`.");
            foreach (var name in drift.PromptsRemoved)
                if (Mentions(draft, name))
                    problems.Add($"The draft still mentions removed prompt `{name}`.");

            var omittedSet = omitted.ToHashSet(StringComparer.Ordinal);
            foreach (var name in drift.ToolsAdded)
                if (!Mentions(draft, name) && !omittedSet.Contains(name))
                    problems.Add($"New tool `{name}` is neither documented nor listed as omitted.");
            foreach (var name in drift.PromptsAdded)
                if (!Mentions(draft, name) && !omittedSet.Contains(name))
                    problems.Add($"New prompt `{name}` is neither documented nor listed as omitted.");
        }

        var allowed = current + "\n" + allowedSourceText;
        foreach (var url in UrlPattern().Matches(draft).Select(m => m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '*', '_')).Distinct())
            if (!allowed.Contains(url, StringComparison.Ordinal))
                problems.Add($"The draft introduces a URL that is not in the current document or the server's catalog: {url}");
        foreach (var email in EmailPattern().Matches(draft).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!allowed.Contains(email, StringComparison.OrdinalIgnoreCase))
                problems.Add($"The draft introduces an email address that is not in the current document or the server's catalog: {email}");

        if (draft.Contains(SkillNotice.StartMarker, StringComparison.Ordinal))
            problems.Add("The draft contains the aggregator's stale-skill notice.");

        return problems;
    }

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="name"/>: in backticks always; as a bare
    /// word only when the name contains <c>_</c>, <c>-</c> or <c>.</c>, so a removed tool called
    /// <c>search</c> is not "found" in ordinary prose. The aggregator's wrapper form
    /// (<c>{server}__{name}</c>) counts as a mention.
    /// </summary>
    internal static bool Mentions(string text, string name)
    {
        if (text.Contains('`' + name + '`', StringComparison.Ordinal)
            || text.Contains("__" + name + '`', StringComparison.Ordinal))
            return true;
        if (name.IndexOfAny(['_', '-', '.']) < 0)
            return false;
        return Regex.IsMatch(text, $"(?:(?<![A-Za-z0-9_])|(?<=__)){Regex.Escape(name)}(?![A-Za-z0-9_])");
    }

    [GeneratedRegex(@"https?://[^\s<>()\[\]`""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}
