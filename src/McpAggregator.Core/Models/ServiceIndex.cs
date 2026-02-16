namespace McpAggregator.Core.Models;

public class ServiceIndex
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public bool HasSkillDocument { get; set; }
    public List<ToolSummary> Tools { get; set; } = [];
}

public class ToolSummary
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public class ServiceDetails
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public bool HasSkillDocument { get; set; }
    public List<ToolDetail> Tools { get; set; } = [];
}

public class ToolDetail
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public object? InputSchema { get; set; }
}
