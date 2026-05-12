using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

public static class SkillSnapshot
{
    public static async Task CaptureAsync(
        ServerRegistry registry,
        ToolIndex toolIndex,
        string serverName,
        CancellationToken ct)
    {
        try
        {
            var tools = await toolIndex.GetToolsForServerAsync(serverName, ct);
            List<PromptDetail> prompts;
            try
            {
                prompts = await toolIndex.GetPromptsForServerAsync(serverName, ct);
            }
            catch
            {
                prompts = [];
            }

            var fingerprint = SkillFingerprint.Compute(
                tools.Select(t => t.Name),
                prompts.Select(p => p.Name));
            var server = registry.Get(serverName);
            await registry.UpdateSkillSnapshotAsync(
                serverName,
                server.RemoteVersion,
                fingerprint,
                DateTimeOffset.UtcNow,
                ct);
        }
        catch
        {
            // Server unreachable — leave the snapshot empty so freshness reports as "unknown".
        }
    }
}
