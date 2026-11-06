using System.Text.Json;
using McpAggregator.Core.Models;

namespace McpAggregator.Core.Services;

/// <summary>
/// Compares a <see cref="SkillBaseline"/> with a server's live catalog (issue #56). Schemas are
/// compared in the canonical form <see cref="SkillFingerprint"/> hashes, so property order never
/// reads as a change, and a catalog that fingerprints the same yields no tool or prompt changes.
/// </summary>
public static class SkillDriftCalculator
{
    public static SkillDrift Compute(
        RegisteredServer server,
        SkillBaseline? baseline,
        IReadOnlyList<ToolDetail> tools,
        IReadOnlyList<PromptDetail> prompts)
    {
        var recordedVersion = baseline?.RemoteVersion ?? server.SkillRecordedVersion;
        var drift = new SkillDrift
        {
            RecordedVersion = recordedVersion,
            CurrentVersion = server.RemoteVersion,
            RecordedAt = server.SkillRecordedAt ?? baseline?.RecordedAt,
            VersionChanged = recordedVersion is not null
                && !string.Equals(recordedVersion, server.RemoteVersion, StringComparison.Ordinal),
            BaselineAvailable = baseline is not null,
        };

        if (baseline is null)
            return drift;

        drift.InstructionsChanged = InstructionsDiffer(baseline.RemoteInstructions, server.RemoteInstructions);

        var oldTools = baseline.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var newTools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        drift.ToolsAdded = newTools.Keys.Except(oldTools.Keys).Order(StringComparer.Ordinal).ToList();
        drift.ToolsRemoved = oldTools.Keys.Except(newTools.Keys).Order(StringComparer.Ordinal).ToList();
        foreach (var name in oldTools.Keys.Intersect(newTools.Keys).Order(StringComparer.Ordinal))
        {
            var before = oldTools[name];
            var after = newTools[name];
            var change = new SkillItemChange
            {
                Name = name,
                DescriptionChanged = !string.Equals(before.Description ?? "", after.Description ?? "", StringComparison.Ordinal),
            };
            CompareSchemas(before.InputSchema, BaselineSchema(after.InputSchema), change);
            if (IsChanged(change))
                drift.ToolsChanged.Add(change);
        }

        var oldPrompts = baseline.Prompts.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var newPrompts = prompts.ToDictionary(p => p.Name, StringComparer.Ordinal);
        drift.PromptsAdded = newPrompts.Keys.Except(oldPrompts.Keys).Order(StringComparer.Ordinal).ToList();
        drift.PromptsRemoved = oldPrompts.Keys.Except(newPrompts.Keys).Order(StringComparer.Ordinal).ToList();
        foreach (var name in oldPrompts.Keys.Intersect(newPrompts.Keys).Order(StringComparer.Ordinal))
        {
            var before = oldPrompts[name];
            var after = newPrompts[name];
            var change = new SkillItemChange
            {
                Name = name,
                DescriptionChanged = !string.Equals(before.Description ?? "", after.Description ?? "", StringComparison.Ordinal),
            };
            var oldArgs = before.Arguments.ToDictionary(a => a.Name, StringComparer.Ordinal);
            var newArgs = after.Arguments.ToDictionary(a => a.Name, StringComparer.Ordinal);
            change.ParametersAdded = newArgs.Keys.Except(oldArgs.Keys).Order(StringComparer.Ordinal).ToList();
            change.ParametersRemoved = oldArgs.Keys.Except(newArgs.Keys).Order(StringComparer.Ordinal).ToList();
            change.ParametersChanged = oldArgs.Keys.Intersect(newArgs.Keys)
                .Where(a => oldArgs[a].Required != newArgs[a].Required
                    || !string.Equals(oldArgs[a].Description ?? "", newArgs[a].Description ?? "", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (IsChanged(change))
                drift.PromptsChanged.Add(change);
        }

        return drift;
    }

    /// <summary>Line endings and outer whitespace are not a change in the server's instructions.</summary>
    internal static bool InstructionsDiffer(string? before, string? after)
        => !string.Equals(NormalizeText(before), NormalizeText(after), StringComparison.Ordinal);

    private static string NormalizeText(string? text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Trim();

    private static JsonElement? BaselineSchema(object? schema) => SkillBaseline.ToElement(schema);

    private static bool IsChanged(SkillItemChange change)
        => change.DescriptionChanged || change.OtherSchemaChanges
           || change.ParametersAdded.Count > 0 || change.ParametersRemoved.Count > 0
           || change.ParametersChanged.Count > 0;

    private static void CompareSchemas(JsonElement? before, JsonElement? after, SkillItemChange change)
    {
        if (string.Equals(Canonical(before), Canonical(after), StringComparison.Ordinal))
            return;

        if (before is not { ValueKind: JsonValueKind.Object } b || after is not { ValueKind: JsonValueKind.Object } a)
        {
            change.OtherSchemaChanges = true;
            return;
        }

        var oldProps = Properties(b);
        var newProps = Properties(a);
        var oldRequired = Required(b);
        var newRequired = Required(a);

        change.ParametersAdded = newProps.Keys.Except(oldProps.Keys).Order(StringComparer.Ordinal).ToList();
        change.ParametersRemoved = oldProps.Keys.Except(newProps.Keys).Order(StringComparer.Ordinal).ToList();
        change.ParametersChanged = oldProps.Keys.Intersect(newProps.Keys)
            .Where(p => !string.Equals(oldProps[p], newProps[p], StringComparison.Ordinal)
                        || oldRequired.Contains(p) != newRequired.Contains(p))
            .Order(StringComparer.Ordinal)
            .ToList();

        change.OtherSchemaChanges = !string.Equals(Remainder(b), Remainder(a), StringComparison.Ordinal);
    }

    private static string Canonical(JsonElement? schema)
        => schema is { } e ? SkillFingerprint.CanonicalJson(e) : string.Empty;

    private static Dictionary<string, string> Properties(JsonElement schema)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
                result[p.Name] = SkillFingerprint.CanonicalJson(p.Value);
        }
        return result;
    }

    private static HashSet<string> Required(JsonElement schema)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String)
                    result.Add(r.GetString()!);
        }
        return result;
    }

    /// <summary>The schema minus <c>properties</c> and <c>required</c>, canonicalized.</summary>
    private static string Remainder(JsonElement schema)
    {
        var rest = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in schema.EnumerateObject())
            if (p.Name is not ("properties" or "required"))
                rest[p.Name] = p.Value;
        return SkillFingerprint.CanonicalJson(JsonSerializer.SerializeToElement(rest));
    }
}
