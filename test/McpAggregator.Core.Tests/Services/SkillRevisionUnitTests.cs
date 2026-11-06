using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// The pure parts of skill revision (issue #57): the diff a reviewer reads, the mechanical checks a
/// draft must pass, and the cleanup of what a model returns.
/// </summary>
[TestClass]
public class SkillRevisionUnitTests
{
    private static readonly SkillMaintenanceOptions Options = new();

    private static SkillDrift Drift(string[]? added = null, string[]? removed = null, string[]? promptsAdded = null)
        => new()
        {
            BaselineAvailable = true,
            ToolsAdded = [.. added ?? []],
            ToolsRemoved = [.. removed ?? []],
            PromptsAdded = [.. promptsAdded ?? []],
        };

    // ---------------------------------------------------------------- LineDiff

    [TestMethod]
    public void Unified_IdenticalText_IsEmpty()
        => Assert.AreEqual(string.Empty, LineDiff.Unified("a\nb\n", "a\r\nb\r\n"));

    [TestMethod]
    public void Unified_ShowsChangesWithContext()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var after = before.Replace("line 10", "line ten").Replace("line 20", "line 20\nline 21");

        var diff = LineDiff.Unified(before, after);

        StringAssert.StartsWith(diff, "--- current\n+++ proposed\n");
        StringAssert.Contains(diff, "-line 10\n+line ten\n");
        StringAssert.Contains(diff, " line 7\n", "Three lines of context before a change.");
        Assert.IsFalse(diff.Contains(" line 6\n"), "Context stops at three lines.");
        StringAssert.Contains(diff, "+line 21\n");
        Assert.AreEqual(2, diff.Split('\n').Count(l => l.StartsWith("@@")), "Changes 10 lines apart are separate hunks.");
    }

    [TestMethod]
    public void Unified_HunkHeaderCountsLines()
    {
        var diff = LineDiff.Unified("a\nb\nc\n", "a\nB\nc\n");

        StringAssert.Contains(diff, "@@ -1,3 +1,3 @@");
    }

    // ---------------------------------------------------------------- validator

    [TestMethod]
    public void Validate_GoodDraft_HasNoProblems()
    {
        var problems = SkillDraftValidator.Validate(
            "# Guide\nUse `create_event`.\n", "# Guide\nUse `create_event` (set `isAllDay` for all-day events) or `get_guide`.\n",
            Drift(added: ["get_guide"]), [], "", Options);

        Assert.AreEqual(0, problems.Count, string.Join(" | ", problems));
    }

    [TestMethod]
    public void Validate_RemovedToolStillMentioned_IsAProblem()
    {
        var problems = SkillDraftValidator.Validate("# G\n", "# G\nCall list_accounts first.\n",
            Drift(removed: ["list_accounts"]), [], "", Options);

        StringAssert.Contains(problems.Single(), "removed tool `list_accounts`");
    }

    [TestMethod]
    public void Validate_RemovedPlainWordTool_OnlyCountsInBackticks()
    {
        Assert.AreEqual(0, SkillDraftValidator.Validate("# G\n", "# G\nYou can search mail.\n",
            Drift(removed: ["search"]), [], "", Options).Count);
        Assert.AreEqual(1, SkillDraftValidator.Validate("# G\n", "# G\nCall `search`.\n",
            Drift(removed: ["search"]), [], "", Options).Count);
    }

    [TestMethod]
    public void Validate_NewToolMissingAndNotOmitted_IsAProblem()
    {
        var problems = SkillDraftValidator.Validate("# G\n", "# G\n", Drift(added: ["get_guide"], promptsAdded: ["daily_briefing"]),
            ["daily_briefing"], "", Options);

        StringAssert.Contains(problems.Single(), "New tool `get_guide`");
    }

    [TestMethod]
    public void Validate_WrapperNameCountsAsAMention()
    {
        Assert.IsTrue(SkillDraftValidator.Mentions("Call `adjutant__get_guide`.", "get_guide"));
        Assert.IsTrue(SkillDraftValidator.Mentions("call adjutant__get_guide with", "get_guide"));
        Assert.IsFalse(SkillDraftValidator.Mentions("call get_guides", "get_guide"));
    }

    [TestMethod]
    public void Validate_NewUrlOrEmail_IsAProblemUnlessInTheSources()
    {
        var problems = SkillDraftValidator.Validate(
            "# G\nDocs: https://example.com/docs\n",
            "# G\nDocs: https://example.com/docs.\nSee https://evil.example/x and mail ops@evil.example or help@known.example.\n",
            Drift(), [], "Contact help@known.example for access.", Options);

        Assert.AreEqual(2, problems.Count, string.Join(" | ", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("https://evil.example/x")));
        Assert.IsTrue(problems.Any(p => p.Contains("ops@evil.example")));
    }

    [TestMethod]
    public void Validate_LengthOutsideBounds_IsAProblem()
    {
        var current = "# G\n" + new string('x', 1000) + "\n";

        Assert.AreEqual(1, SkillDraftValidator.Validate(current, "# G\nshort\n", Drift(), [], "", Options).Count);
        Assert.AreEqual(1, SkillDraftValidator.Validate(current, current + new string('y', 3000), Drift(), [], "", Options).Count);
        Assert.AreEqual(0, SkillDraftValidator.Validate("# tiny\n", "# tiny\n" + new string('y', 300), Drift(), [], "", Options).Count,
            "Short documents are not held to the ratio.");
    }

    [TestMethod]
    public void Validate_EmptyDraft_IsAProblem()
        => StringAssert.Contains(SkillDraftValidator.Validate("# G\n", "  \n", Drift(), [], "", Options).Single(), "empty");

    // ---------------------------------------------------------------- model output cleanup

    [TestMethod]
    public void Clean_StripsFenceAndReadsOmittedNotes()
    {
        const string raw = """
            ```markdown
            # Guide

            Use `create_event`.
            ```

            <!-- skill-revision-notes
            omitted: get_unsubscribe_info - covered by the bulk_unsubscribe prompt
            omitted: `send-later`: not for agents
            omitted: plain_name
            -->
            """;

        var (markdown, omitted) = SkillMaintainer.Clean(raw);

        Assert.AreEqual("# Guide\n\nUse `create_event`.\n", markdown);
        CollectionAssert.AreEqual(new[] { "get_unsubscribe_info", "send-later", "plain_name" }, omitted.Select(o => o.Name).ToList());
        Assert.AreEqual("covered by the bulk_unsubscribe prompt", omitted[0].Reason);
        Assert.AreEqual("not for agents", omitted[1].Reason);
    }

    [TestMethod]
    public void Clean_RemovesNoticeAndOldProvenance()
    {
        var raw = SkillNotice.StartMarker + "\n> stale\n" + SkillNotice.EndMarker + "\n\n# Guide\nText.\n\n"
                  + SkillMaintainer.ProvenanceMarker + "\n_Revised automatically for x 1.0 on 2026-01-01._\n";

        Assert.AreEqual("# Guide\nText.\n", SkillMaintainer.Clean(raw).Markdown);
    }

    [TestMethod]
    public void StripProvenance_KeepsTextAfterIt()
    {
        var doc = "# Guide\n\n" + SkillMaintainer.ProvenanceMarker + "\n_Revised automatically._\n\n## Later section\n";

        Assert.AreEqual("# Guide\n\n## Later section\n", SkillMaintainer.StripProvenance(doc));
    }
}
