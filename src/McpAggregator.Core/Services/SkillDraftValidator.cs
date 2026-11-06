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

        // Exact set membership, both sides extracted and normalized the same way: a substring test
        // would let https://example.com pass because https://example.com.au/guide is allowed.
        var allowed = current + "\n" + allowedSourceText;
        var allowedUrls = Urls(allowed).ToHashSet(StringComparer.Ordinal);
        foreach (var url in Urls(draft).Distinct(StringComparer.Ordinal))
            if (!allowedUrls.Contains(url))
                problems.Add($"The draft introduces a URL that is not in the current document or the server's catalog: {url}");
        var allowedEmails = Emails(allowed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var email in Emails(draft).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!allowedEmails.Contains(email))
                problems.Add($"The draft introduces an email address that is not in the current document or the server's catalog: {email}");

        if (draft.Contains(SkillNotice.StartMarker, StringComparison.Ordinal))
            problems.Add("The draft contains the aggregator's stale-skill notice.");

        return problems;
    }

    private static IEnumerable<string> Urls(string text)
        => UrlPattern().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '*', '_'));

    private static IEnumerable<string> Emails(string text)
        => EmailPattern().Matches(text).Select(m => m.Value);

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="name"/>. Names are compared as whole
    /// identifiers (letters, digits, <c>_</c>, <c>-</c>, <c>.</c>; a trailing <c>.</c> or <c>-</c> is
    /// sentence punctuation), so <c>foo-bar-v2</c> is not a mention of <c>foo-bar</c>. A name counts
    /// inside a code span, and as the aggregator's wrapper form <c>{server}__{name}</c> anywhere. A
    /// bare identifier in prose counts only when the name contains <c>_</c>, <c>-</c> or <c>.</c>, so
    /// a removed tool called <c>search</c> is not "found" in ordinary prose.
    /// </summary>
    internal static bool Mentions(string text, string name)
    {
        foreach (Match span in CodeSpanPattern().Matches(text))
            if (Identifiers(span.Groups[1].Value).Any(id => id == name || IsWrapperOf(id, name)))
                return true;

        var plainWord = name.IndexOfAny(['_', '-', '.']) < 0;
        return Identifiers(text).Any(id => IsWrapperOf(id, name) || (!plainWord && id == name));
    }

    private static bool IsWrapperOf(string identifier, string name)
        => identifier.Length > name.Length + 2 && identifier.EndsWith("__" + name, StringComparison.Ordinal);

    private static IEnumerable<string> Identifiers(string text)
        => IdentifierPattern().Matches(text).Select(m => m.Value.TrimEnd('.', '-'));

    [GeneratedRegex(@"[A-Za-z0-9_.\-]+")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeSpanPattern();

    [GeneratedRegex(@"https?://[^\s<>()\[\]`""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}
