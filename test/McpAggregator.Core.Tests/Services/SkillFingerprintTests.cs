using System.Text.Json;
using System.Text.RegularExpressions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// <see cref="SkillFingerprint"/> is what anchors a skill document to the downstream surface it
/// was written against (issue #28); issue #41 made it cover descriptions and schemas, not names only.
/// </summary>
[TestClass]
public partial class SkillFingerprintTests
{
    private static ToolDetail Tool(string name, string? description = null, string? schemaJson = null)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = schemaJson is null ? null : JsonDocument.Parse(schemaJson).RootElement
        };

    private static PromptDetail Prompt(string name, string? description = null, params (string Name, string? Description, bool Required)[] args)
        => new()
        {
            Name = name,
            Description = description,
            Arguments = args.Select(a => new PromptArgumentDetail { Name = a.Name, Description = a.Description, Required = a.Required }).ToList()
        };

    private static ToolDetail[] Tools(params string[] names) => names.Select(n => Tool(n)).ToArray();

    private static PromptDetail[] Prompts(params string[] names) => names.Select(n => Prompt(n)).ToArray();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FullHex();

    [TestMethod]
    public void Compute_IsDeterministic_ForSameInput()
    {
        var a = SkillFingerprint.Compute(Tools("tool_a", "tool_b"), Prompts("prompt_x"));
        var b = SkillFingerprint.Compute(Tools("tool_a", "tool_b"), Prompts("prompt_x"));

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_Returns64LowercaseHex()
    {
        var fingerprint = SkillFingerprint.Compute(Tools("tool_a"), []);

        Assert.IsTrue(FullHex().IsMatch(fingerprint), $"Expected a full lowercase SHA-256, got '{fingerprint}'.");
    }

    [TestMethod]
    public void Compute_IsOrderIndependent_ForTools()
    {
        var a = SkillFingerprint.Compute(Tools("tool_a", "tool_b"), []);
        var b = SkillFingerprint.Compute(Tools("tool_b", "tool_a"), []);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_IsOrderIndependent_ForPrompts()
    {
        var a = SkillFingerprint.Compute([], Prompts("p1", "p2"));
        var b = SkillFingerprint.Compute([], Prompts("p2", "p1"));

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolAdded()
    {
        var before = SkillFingerprint.Compute(Tools("tool_a"), []);
        var after = SkillFingerprint.Compute(Tools("tool_a", "tool_b"), []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolRemoved()
    {
        var before = SkillFingerprint.Compute(Tools("tool_a", "tool_b"), []);
        var after = SkillFingerprint.Compute(Tools("tool_a"), []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolRenamed()
    {
        var before = SkillFingerprint.Compute(Tools("tool_a"), []);
        var after = SkillFingerprint.Compute(Tools("tool_renamed"), []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenOnlyToolDescriptionChanges()
    {
        // The issue #41 regression: a description is what a model reads to pick a tool, so a
        // downstream that rewrites it must read as drift even with the same name and schema.
        const string schema = """{"type":"object","properties":{"q":{"type":"string"}}}""";
        var before = SkillFingerprint.Compute([Tool("search", "Search the index.", schema)], []);
        var after = SkillFingerprint.Compute([Tool("search", "Search the index; now requires auth.", schema)], []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenOnlyInputSchemaChanges()
    {
        var before = SkillFingerprint.Compute(
            [Tool("search", "Search.", """{"type":"object","properties":{"q":{"type":"string"}}}""")], []);
        var after = SkillFingerprint.Compute(
            [Tool("search", "Search.", """{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}""")], []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_IsEqual_WhenSchemaKeysAreReordered_IncludingNestedObjects()
    {
        var a = SkillFingerprint.Compute(
            [Tool("search", "Search.", """{"type":"object","properties":{"q":{"type":"string","description":"Query"},"limit":{"type":"integer"}},"required":["q"]}""")], []);
        var b = SkillFingerprint.Compute(
            [Tool("search", "Search.", """{"required":["q"],"properties":{"limit":{"type":"integer"},"q":{"description":"Query","type":"string"}},"type":"object"}""")], []);

        Assert.AreEqual(a, b, "Property order is not a wire-level change and must not flap the fingerprint.");
    }

    [TestMethod]
    public void Compute_Differs_WhenSchemaArrayOrderChanges()
    {
        // Arrays are ordered in JSON; only object keys are canonicalized.
        var a = SkillFingerprint.Compute([Tool("t", null, """{"required":["a","b"]}""")], []);
        var b = SkillFingerprint.Compute([Tool("t", null, """{"required":["b","a"]}""")], []);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void Compute_TreatsNullSchemaAndEmptyObjectAsDistinct()
    {
        var withoutSchema = SkillFingerprint.Compute([Tool("t", "d", null)], []);
        var withEmptySchema = SkillFingerprint.Compute([Tool("t", "d", "{}")], []);

        Assert.AreNotEqual(withoutSchema, withEmptySchema);
    }

    [TestMethod]
    public void Compute_TreatsNullDescriptionAndEmptyDescriptionAsEqual()
    {
        // Both mean "no description" to a model; documented so nobody relies on the difference.
        var nullDescription = SkillFingerprint.Compute([Tool("t", null, "{}")], [Prompt("p", null)]);
        var emptyDescription = SkillFingerprint.Compute([Tool("t", "", "{}")], [Prompt("p", "")]);

        Assert.AreEqual(nullDescription, emptyDescription);
    }

    [TestMethod]
    public void Compute_AcceptsNonJsonElementSchemaObjects()
    {
        // ToolIndex stores a JsonElement; a caller handing over any other object gets it serialized.
        var fromElement = SkillFingerprint.Compute([Tool("t", "d", """{"type":"object"}""")], []);
        var fromObject = SkillFingerprint.Compute(
            [new ToolDetail { Name = "t", Description = "d", InputSchema = new Dictionary<string, object> { ["type"] = "object" } }], []);

        Assert.AreEqual(fromElement, fromObject);
    }

    [TestMethod]
    public void Compute_Differs_WhenPromptChanges()
    {
        var before = SkillFingerprint.Compute(Tools("t"), Prompts("p1"));
        var after = SkillFingerprint.Compute(Tools("t"), Prompts("p2"));

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenOnlyPromptDescriptionChanges()
    {
        var before = SkillFingerprint.Compute([], [Prompt("summarize", "Summarize text.")]);
        var after = SkillFingerprint.Compute([], [Prompt("summarize", "Summarize text in bullet points.")]);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenPromptArgumentDescriptionChanges()
    {
        var before = SkillFingerprint.Compute([], [Prompt("summarize", "S", ("text", "The text", true))]);
        var after = SkillFingerprint.Compute([], [Prompt("summarize", "S", ("text", "The text, markdown", true))]);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenPromptArgumentRequiredChanges()
    {
        var before = SkillFingerprint.Compute([], [Prompt("summarize", "S", ("text", "The text", true))]);
        var after = SkillFingerprint.Compute([], [Prompt("summarize", "S", ("text", "The text", false))]);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_IsOrderIndependent_ForPromptArguments()
    {
        var a = SkillFingerprint.Compute([], [Prompt("p", "d", ("a", "A", true), ("b", "B", false))]);
        var b = SkillFingerprint.Compute([], [Prompt("p", "d", ("b", "B", false), ("a", "A", true))]);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_DistinguishesToolFromPrompt()
    {
        // Same name in tools vs prompts should produce different fingerprints —
        // a downstream that renames a tool to a prompt counts as drift.
        var asTool = SkillFingerprint.Compute(Tools("greet"), []);
        var asPrompt = SkillFingerprint.Compute([], Prompts("greet"));

        Assert.AreNotEqual(asTool, asPrompt);
    }

    [TestMethod]
    public void Compute_EmptyInputs_StillProducesStableHash()
    {
        var a = SkillFingerprint.Compute([], []);
        var b = SkillFingerprint.Compute([], []);

        Assert.AreEqual(a, b);
        Assert.IsTrue(FullHex().IsMatch(a));
    }

    // ---------------------------------------------------------------- Matches

    [TestMethod]
    public void Matches_WithFullLengthFingerprint_UsesTheCurrentAlgorithm()
    {
        var tools = new[] { Tool("search", "Search.", """{"type":"object"}""") };
        var recorded = SkillFingerprint.Compute(tools, []);

        Assert.IsTrue(SkillFingerprint.Matches(recorded, tools, []));
        Assert.IsFalse(SkillFingerprint.Matches(recorded, [Tool("search", "Search, changed.", """{"type":"object"}""")], []),
            "A description change must be detected by the current algorithm.");
    }

    // The pre-#41 algorithm, reproduced here so the legacy path is pinned to what old registries
    // actually contain rather than to whatever the private helper does today.
    private static string LegacyFingerprint(IEnumerable<string> toolNames, IEnumerable<string> promptNames)
    {
        var input = string.Join("\n", toolNames.OrderBy(n => n, StringComparer.Ordinal))
            + "\n--\n" + string.Join("\n", promptNames.OrderBy(n => n, StringComparer.Ordinal));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    [TestMethod]
    public void Matches_WithLegacy16HexFingerprint_ComparesNamesOnly()
    {
        var legacy = LegacyFingerprint(["search", "fetch"], ["summarize"]);
        Assert.AreEqual(16, legacy.Length);

        // Descriptions and schemas were never part of a legacy fingerprint, so they must not
        // make a registry written before #41 read stale on upgrade.
        var sameNames = new[] { Tool("fetch", "Fetch a page.", """{"type":"object"}"""), Tool("search", "Search.", "{}") };
        Assert.IsTrue(SkillFingerprint.Matches(legacy, sameNames, [Prompt("summarize", "Summarize.", ("text", "T", true))]));

        // A rename was always drift.
        Assert.IsFalse(SkillFingerprint.Matches(legacy, [Tool("fetch"), Tool("search_v2")], [Prompt("summarize")]));
        Assert.IsFalse(SkillFingerprint.Matches(legacy, sameNames, [Prompt("summarise")]));
    }

    [TestMethod]
    public void Matches_WithUnrecognisedFingerprint_IsFalse()
    {
        Assert.IsFalse(SkillFingerprint.Matches("not-a-fingerprint", Tools("t"), []));
        Assert.IsFalse(SkillFingerprint.Matches(new string('z', 16), Tools("t"), []), "16 non-hex characters are not a legacy fingerprint.");
    }
}
