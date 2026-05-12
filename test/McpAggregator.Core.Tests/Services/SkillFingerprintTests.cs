using McpAggregator.Core.Services;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class SkillFingerprintTests
{
    [TestMethod]
    public void Compute_IsDeterministic_ForSameInput()
    {
        var a = SkillFingerprint.Compute(["tool_a", "tool_b"], ["prompt_x"]);
        var b = SkillFingerprint.Compute(["tool_a", "tool_b"], ["prompt_x"]);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_IsOrderIndependent_ForTools()
    {
        var a = SkillFingerprint.Compute(["tool_a", "tool_b"], []);
        var b = SkillFingerprint.Compute(["tool_b", "tool_a"], []);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_IsOrderIndependent_ForPrompts()
    {
        var a = SkillFingerprint.Compute([], ["p1", "p2"]);
        var b = SkillFingerprint.Compute([], ["p2", "p1"]);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolAdded()
    {
        var before = SkillFingerprint.Compute(["tool_a"], []);
        var after = SkillFingerprint.Compute(["tool_a", "tool_b"], []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolRemoved()
    {
        var before = SkillFingerprint.Compute(["tool_a", "tool_b"], []);
        var after = SkillFingerprint.Compute(["tool_a"], []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenToolRenamed()
    {
        var before = SkillFingerprint.Compute(["tool_a"], []);
        var after = SkillFingerprint.Compute(["tool_renamed"], []);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_Differs_WhenPromptChanges()
    {
        var before = SkillFingerprint.Compute(["t"], ["p1"]);
        var after = SkillFingerprint.Compute(["t"], ["p2"]);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void Compute_DistinguishesToolFromPrompt()
    {
        // Same name in tools vs prompts should produce different fingerprints —
        // a downstream that renames a tool to a prompt counts as drift.
        var asTool = SkillFingerprint.Compute(["greet"], []);
        var asPrompt = SkillFingerprint.Compute([], ["greet"]);

        Assert.AreNotEqual(asTool, asPrompt);
    }

    [TestMethod]
    public void Compute_EmptyInputs_StillProducesStableHash()
    {
        var a = SkillFingerprint.Compute([], []);
        var b = SkillFingerprint.Compute([], []);

        Assert.AreEqual(a, b);
        Assert.IsFalse(string.IsNullOrEmpty(a));
    }
}
