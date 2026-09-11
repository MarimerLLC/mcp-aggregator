using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

public static class SkillSnapshot
{
    /// <summary>The note callers append to their success message when <see cref="CaptureAsync"/> returns <c>false</c>.</summary>
    public const string NoBaselineNote =
        "The server could not be reached, so no freshness baseline was recorded; " +
        "freshness will report 'unknown' until the skill is saved again while the server is reachable.";

    /// <summary>
    /// Records the server's current tools, prompts and version as the baseline a freshly saved skill
    /// document is compared against. Returns <c>true</c> when a baseline was recorded and
    /// <c>false</c> when the server's tools or prompts could not be read; in that case any earlier
    /// snapshot is cleared so the new document is not anchored to a baseline it was never written
    /// against, and freshness reads <c>unknown</c> until the skill is saved again while the server is
    /// reachable. A failure to persist the registry propagates.
    /// </summary>
    public static async Task<bool> CaptureAsync(
        ServerRegistry registry,
        ToolIndex toolIndex,
        string serverName,
        CancellationToken ct)
    {
        List<ToolDetail> tools;
        List<PromptDetail> prompts;
        try
        {
            tools = await toolIndex.GetToolsForServerAsync(serverName, ct);
            // A server without prompt support yields an empty list from the index; anything that
            // still throws is a genuine failure and must not be baked into the baseline as "no prompts".
            prompts = await toolIndex.GetPromptsForServerAsync(serverName, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await registry.UpdateSkillSnapshotAsync(serverName, null, null, null, ct);
            return false;
        }

        var fingerprint = SkillFingerprint.Compute(tools, prompts);
        var server = registry.Get(serverName);
        await registry.UpdateSkillSnapshotAsync(
            serverName,
            server.RemoteVersion,
            fingerprint,
            DateTimeOffset.UtcNow,
            ct);
        return true;
    }
}
