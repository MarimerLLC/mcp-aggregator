using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

/// <summary>
/// The snapshot a skill document is anchored to (issue #28, tightened in issue #41). The fingerprint
/// covers everything a model reads when it picks and calls a tool or prompt: per tool the name,
/// description and input schema (canonicalized so property order does not matter); per prompt the
/// name, description and each argument's name, description and required flag. It is a full SHA-256
/// (64 lowercase hex characters).
/// <para>
/// Fingerprints recorded before #41 are 16 hex characters and hash tool and prompt <em>names</em>
/// only. <see cref="Matches"/> recognises that legacy format and compares with the old algorithm so
/// an upgrade does not flip every skill to <c>stale</c>; the next <c>update_skill</c> records the
/// current format.
/// </para>
/// </summary>
public static class SkillFingerprint
{
    private const char FieldSeparator = '';
    private const char RecordSeparator = '';
    private const string SectionSeparator = "\n--\n";
    private const int LegacyLength = 16;

    public static string Compute(IEnumerable<ToolDetail> tools, IEnumerable<PromptDetail> prompts)
    {
        var sb = new StringBuilder();

        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(tool.Name).Append(FieldSeparator);
            sb.Append(tool.Description ?? string.Empty).Append(FieldSeparator);
            sb.Append(CanonicalJson(tool.InputSchema)).Append(RecordSeparator);
        }

        sb.Append(SectionSeparator);

        foreach (var prompt in prompts.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(prompt.Name).Append(FieldSeparator);
            sb.Append(prompt.Description ?? string.Empty).Append(FieldSeparator);
            foreach (var arg in prompt.Arguments.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                sb.Append(arg.Name).Append(FieldSeparator);
                sb.Append(arg.Description ?? string.Empty).Append(FieldSeparator);
                sb.Append(arg.Required ? '1' : '0').Append(FieldSeparator);
            }
            sb.Append(RecordSeparator);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// True when <paramref name="recorded"/> describes the given tools and prompts. A 16-hex legacy
    /// value is compared against the names-only algorithm it was produced by.
    /// </summary>
    public static bool Matches(string recorded, IEnumerable<ToolDetail> tools, IEnumerable<PromptDetail> prompts)
    {
        var current = IsLegacy(recorded)
            ? ComputeLegacy(tools.Select(t => t.Name), prompts.Select(p => p.Name))
            : Compute(tools, prompts);
        return string.Equals(current, recorded, StringComparison.Ordinal);
    }

    private static bool IsLegacy(string fingerprint)
        => fingerprint.Length == LegacyLength && fingerprint.All(Uri.IsHexDigit);

    /// <summary>The pre-#41 algorithm, kept verbatim so legacy registries keep reading <c>fresh</c>.</summary>
    private static string ComputeLegacy(IEnumerable<string> toolNames, IEnumerable<string> promptNames)
    {
        var sortedTools = toolNames.OrderBy(n => n, StringComparer.Ordinal);
        var sortedPrompts = promptNames.OrderBy(n => n, StringComparer.Ordinal);
        var input = string.Join("\n", sortedTools) + "\n--\n" + string.Join("\n", sortedPrompts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..LegacyLength].ToLowerInvariant();
    }

    /// <summary>
    /// The schema as compact JSON with object properties sorted by key at every level, so a
    /// downstream that reorders properties between reconnects does not read as drift. A null schema
    /// yields an empty string, which is distinct from <c>{}</c>.
    /// </summary>
    private static string CanonicalJson(object? schema)
    {
        if (schema is null)
            return string.Empty;

        var element = schema is JsonElement je ? je : JsonSerializer.SerializeToElement(schema);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }
}
