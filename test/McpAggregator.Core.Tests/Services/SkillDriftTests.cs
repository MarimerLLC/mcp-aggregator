using System.Text.Json;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// <see cref="SkillDriftCalculator"/> and <see cref="SkillNotice"/> (issue #56): what changed since a
/// skill was written, and how that is told to the caller in front of the document.
/// </summary>
[TestClass]
public class SkillDriftTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolDetail Tool(string name, string description, string schema)
        => new() { Name = name, Description = description, InputSchema = Schema(schema) };

    private static PromptDetail Prompt(string name, string description, params (string Name, bool Required)[] args)
        => new()
        {
            Name = name,
            Description = description,
            Arguments = args.Select(a => new PromptArgumentDetail { Name = a.Name, Required = a.Required }).ToList(),
        };

    private const string EventSchema =
        """{"type":"object","properties":{"subject":{"type":"string"},"start":{"type":"string"}},"required":["subject","start"]}""";

    private static RegisteredServer Server(string? version = "1.0.0", string? instructions = "Use list_accounts first.")
    {
        var server = TestHelpers.HttpServer("adjutant");
        server.RemoteVersion = version;
        server.RemoteInstructions = instructions;
        server.HasSkillDocument = true;
        server.SkillRecordedVersion = "1.0.0";
        server.SkillRecordedAt = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        return server;
    }

    private static SkillBaseline Baseline(IEnumerable<ToolDetail> tools, IEnumerable<PromptDetail>? prompts = null)
        => SkillBaseline.Capture(Server(), tools, prompts ?? [], new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));

    // ---------------------------------------------------------------- calculator

    [TestMethod]
    public void Compute_IdenticalCatalog_ReportsNothing()
    {
        var tools = new List<ToolDetail> { Tool("create_event", "Create an event.", EventSchema) };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline(tools), tools, []);

        Assert.IsTrue(drift.BaselineAvailable);
        Assert.IsFalse(drift.VersionChanged);
        Assert.IsTrue(drift.CatalogUnchanged);
    }

    [TestMethod]
    public void Compute_PropertyOrderAndWhitespace_AreNotChanges()
    {
        var before = new List<ToolDetail> { Tool("create_event", "Create an event.", EventSchema) };
        var after = new List<ToolDetail>
        {
            Tool("create_event", "Create an event.",
                """{ "required": ["subject","start"], "properties": { "start": {"type":"string"}, "subject": {"type":"string"} }, "type": "object" }"""),
        };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline(before), after, []);

        Assert.IsTrue(drift.CatalogUnchanged);
    }

    [TestMethod]
    public void Compute_ToolsAddedAndRemoved_AreListedByName()
    {
        var before = new List<ToolDetail> { Tool("a", "A", "{}"), Tool("gone", "G", "{}") };
        var after = new List<ToolDetail> { Tool("a", "A", "{}"), Tool("new_one", "N", "{}") };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline(before), after, []);

        CollectionAssert.AreEqual(new[] { "new_one" }, drift.ToolsAdded);
        CollectionAssert.AreEqual(new[] { "gone" }, drift.ToolsRemoved);
        Assert.AreEqual(0, drift.ToolsChanged.Count);
    }

    [TestMethod]
    public void Compute_ParameterChanges_AreClassified()
    {
        var before = new List<ToolDetail> { Tool("create_event", "Create an event.", EventSchema) };
        var after = new List<ToolDetail>
        {
            Tool("create_event", "Create an event, or an all-day event.",
                """{"type":"object","properties":{"subject":{"type":"string","maxLength":200},"start":{"type":"string"},"isAllDay":{"type":"boolean"}},"required":["subject"]}"""),
        };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline(before), after, []);

        var change = drift.ToolsChanged.Single();
        Assert.AreEqual("create_event", change.Name);
        Assert.IsTrue(change.DescriptionChanged);
        CollectionAssert.AreEqual(new[] { "isAllDay" }, change.ParametersAdded);
        Assert.AreEqual(0, change.ParametersRemoved.Count);
        // subject: sub-schema changed; start: no longer required.
        CollectionAssert.AreEqual(new[] { "start", "subject" }, change.ParametersChanged);
        Assert.IsFalse(change.OtherSchemaChanges);
    }

    [TestMethod]
    public void Compute_TopLevelSchemaKeyword_IsOtherSchemaChange()
    {
        var before = new List<ToolDetail> { Tool("t", "T", """{"type":"object","properties":{}}""") };
        var after = new List<ToolDetail> { Tool("t", "T", """{"type":"object","properties":{},"additionalProperties":false}""") };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline(before), after, []);

        var change = drift.ToolsChanged.Single();
        Assert.IsTrue(change.OtherSchemaChanges);
        Assert.AreEqual(0, change.ParametersAdded.Count + change.ParametersRemoved.Count + change.ParametersChanged.Count);
    }

    [TestMethod]
    public void Compute_PromptArgumentChanges_AreClassified()
    {
        var before = new List<PromptDetail> { Prompt("daily_briefing", "Brief me.", ("date", false)), Prompt("old", "O") };
        var after = new List<PromptDetail> { Prompt("daily_briefing", "Brief me.", ("date", true), ("timeZone", true)) };

        var drift = SkillDriftCalculator.Compute(Server(), Baseline([], before), [], after);

        CollectionAssert.AreEqual(new[] { "old" }, drift.PromptsRemoved);
        var change = drift.PromptsChanged.Single();
        CollectionAssert.AreEqual(new[] { "timeZone" }, change.ParametersAdded);
        CollectionAssert.AreEqual(new[] { "date" }, change.ParametersChanged);
        Assert.IsFalse(change.DescriptionChanged);
    }

    [TestMethod]
    public void Compute_InstructionsLineEndings_AreNotAChange()
    {
        var baseline = Baseline([]);
        baseline.RemoteInstructions = "Line one.\r\nLine two.\r\n";

        var drift = SkillDriftCalculator.Compute(Server(instructions: "Line one.\nLine two."), baseline, [], []);

        Assert.IsFalse(drift.InstructionsChanged);
    }

    [TestMethod]
    public void Compute_InstructionsEdited_IsAChange()
    {
        var drift = SkillDriftCalculator.Compute(Server(instructions: "Call get_guide first."), Baseline([]), [], []);

        Assert.IsTrue(drift.InstructionsChanged);
        Assert.IsFalse(drift.CatalogUnchanged);
    }

    [TestMethod]
    public void Compute_WithoutBaseline_ComparesVersionOnly()
    {
        var drift = SkillDriftCalculator.Compute(Server(version: "2.0.0"), baseline: null,
            [Tool("new_tool", "N", "{}")], []);

        Assert.IsFalse(drift.BaselineAvailable);
        Assert.IsTrue(drift.VersionChanged);
        Assert.AreEqual("1.0.0", drift.RecordedVersion);
        Assert.AreEqual("2.0.0", drift.CurrentVersion);
        Assert.AreEqual(0, drift.ToolsAdded.Count, "Without a baseline nothing can be claimed about the catalog.");
        Assert.IsFalse(drift.CatalogUnchanged);
    }

    // ---------------------------------------------------------------- notice

    [TestMethod]
    public void Render_NamesVersionsAndChanges()
    {
        var before = new List<ToolDetail> { Tool("create_event", "Create an event.", EventSchema) };
        var after = new List<ToolDetail>
        {
            Tool("create_event", "Create an event.",
                """{"type":"object","properties":{"subject":{"type":"string"},"start":{"type":"string"},"isAllDay":{"type":"boolean"}},"required":["subject","start"]}"""),
            Tool("get_guide", "Guides.", "{}"),
        };
        var drift = SkillDriftCalculator.Compute(Server(version: "1.8.4"), Baseline(before), after, []);

        var notice = SkillNotice.Render(drift);

        StringAssert.StartsWith(notice, SkillNotice.StartMarker);
        StringAssert.Contains(notice, "written for version 1.0.0 (2026-09-09); the server now reports 1.8.4");
        StringAssert.Contains(notice, "new tool `get_guide`");
        StringAssert.Contains(notice, "tool `create_event`: new parameter `isAllDay`");
        StringAssert.Contains(notice, "trust the schemas");
        StringAssert.Contains(notice, SkillNotice.EndMarker);
    }

    [TestMethod]
    public void Render_CapsTheChangeList()
    {
        var after = Enumerable.Range(1, 20).Select(i => Tool($"tool_{i:00}", "T", "{}")).ToList();
        var drift = SkillDriftCalculator.Compute(Server(), Baseline([]), after, []);

        var notice = SkillNotice.Render(drift);

        Assert.AreEqual(SkillNotice.MaxChangeLines, notice.Split('\n').Count(l => l.StartsWith("> - new tool")));
        StringAssert.Contains(notice, "and 8 more");
    }

    [TestMethod]
    public void Render_VersionOnlyDrift_SaysTheCatalogIsUnchanged()
    {
        var tools = new List<ToolDetail> { Tool("a", "A", "{}") };
        var drift = SkillDriftCalculator.Compute(Server(version: "2.0.0"), Baseline(tools), tools, []);

        StringAssert.Contains(SkillNotice.Render(drift), "only the version differs");
    }

    [TestMethod]
    public void Render_WithoutBaseline_SaysChangesWereNotRecorded()
    {
        var drift = SkillDriftCalculator.Compute(Server(version: "2.0.0"), null, [], []);

        StringAssert.Contains(SkillNotice.Render(drift), "was not recorded");
    }

    [TestMethod]
    public void Strip_RemovesARenderedNotice()
    {
        const string document = "# Adjutant Skill Guide\n\nCall list_accounts first.\n";
        var drift = SkillDriftCalculator.Compute(Server(version: "2.0.0"), null, [], []);

        Assert.AreEqual(document, SkillNotice.Strip(SkillNotice.Render(drift) + document));
    }

    [TestMethod]
    public void Strip_LeavesOtherDocumentsAlone()
    {
        const string document = "# Guide\n\n<!-- aggregator:skill-notice -->\nnot at the start\n<!-- /aggregator:skill-notice -->\n";

        Assert.AreEqual(document, SkillNotice.Strip(document));
        Assert.AreEqual("  # Guide", SkillNotice.Strip("  # Guide"));
    }

    [TestMethod]
    public void Strip_UnterminatedNotice_IsLeftAlone()
    {
        var document = SkillNotice.StartMarker + "\n> half a notice\n# Guide\n";

        Assert.AreEqual(document, SkillNotice.Strip(document));
    }
}
