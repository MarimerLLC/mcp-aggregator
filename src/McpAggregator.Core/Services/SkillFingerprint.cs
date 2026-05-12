using System.Security.Cryptography;
using System.Text;

namespace McpAggregator.Core.Services;

public static class SkillFingerprint
{
    public static string Compute(IEnumerable<string> toolNames, IEnumerable<string> promptNames)
    {
        var sortedTools = toolNames.OrderBy(n => n, StringComparer.Ordinal);
        var sortedPrompts = promptNames.OrderBy(n => n, StringComparer.Ordinal);
        var input = string.Join("\n", sortedTools) + "\n--\n" + string.Join("\n", sortedPrompts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
