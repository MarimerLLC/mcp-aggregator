namespace McpAggregator.Core.Services;

/// <summary>
/// The aggregator's own skill document as this build ships it: <c>data/skills/mcp-aggregator.md</c>,
/// embedded in <c>McpAggregator.Core</c> (issue #72). <see cref="SelfSkillSeeder"/> uses it to keep the
/// live copy current across releases.
/// </summary>
public static class ShippedSelfSkill
{
    internal const string ResourceName = "McpAggregator.Core.Skills.mcp-aggregator.md";

    private static readonly Lazy<string?> Shipped = new(Load);

    /// <summary>The shipped document, or null if the build carries none.</summary>
    public static string? Text => Shipped.Value;

    /// <summary>
    /// SHA-256 of the text with line endings normalized to <c>\n</c>, lowercase hex. A checkout on
    /// Windows embeds CRLF and one on Linux LF; the two must count as the same version.
    /// </summary>
    public static string Hash(string text) => SkillStore.ContentHash(text.ReplaceLineEndings("\n"));

    private static string? Load()
    {
        using var stream = typeof(ShippedSelfSkill).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
