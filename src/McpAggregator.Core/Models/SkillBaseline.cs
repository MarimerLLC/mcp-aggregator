using System.Text.Json;

namespace McpAggregator.Core.Models;

/// <summary>
/// The downstream catalog a skill document was written against (issue #56), stored next to the
/// document as <c>skills/{name}.baseline.json</c> whenever <see cref="Services.SkillSnapshot"/>
/// records a baseline. The fingerprint on <see cref="RegisteredServer"/> says <em>whether</em> the
/// catalog drifted; this says <em>how</em>, so callers can be told what changed
/// (<see cref="SkillDrift"/>) and a later revision has something to work from.
/// </summary>
public class SkillBaseline
{
    public string? RemoteVersion { get; set; }
    public string? RemoteInstructions { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public List<BaselineTool> Tools { get; set; } = [];
    public List<BaselinePrompt> Prompts { get; set; } = [];

    public static SkillBaseline Capture(
        RegisteredServer server,
        IEnumerable<ToolDetail> tools,
        IEnumerable<PromptDetail> prompts,
        DateTimeOffset recordedAt) => new()
    {
        RemoteVersion = server.RemoteVersion,
        RemoteInstructions = server.RemoteInstructions,
        RecordedAt = recordedAt,
        Tools = tools.Select(t => new BaselineTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = ToElement(t.InputSchema),
        }).OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
        Prompts = prompts.Select(p => new BaselinePrompt
        {
            Name = p.Name,
            Description = p.Description,
            Arguments = p.Arguments.Select(a => new PromptArgumentDetail
            {
                Name = a.Name,
                Description = a.Description,
                Required = a.Required,
            }).ToList(),
        }).OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
    };

    internal static JsonElement? ToElement(object? schema) => schema switch
    {
        null => null,
        JsonElement je => je.Clone(),
        _ => JsonSerializer.SerializeToElement(schema),
    };
}

public class BaselineTool
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public JsonElement? InputSchema { get; set; }
}

public class BaselinePrompt
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<PromptArgumentDetail> Arguments { get; set; } = [];
}
