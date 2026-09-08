namespace McpAggregator.Core.Configuration;

public class AggregatorOptions
{
    public const string SectionName = "McpAggregator";

    public string DataDirectory { get; set; } = "data";
    public string RegistryFile { get; set; } = "registry.json";
    public string SkillsDirectory { get; set; } = "skills";
    public TimeSpan IndexCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan DefaultToolTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When downstream tools are exposed as typed wrapper tools in the aggregator's own tool list.
    /// Defaults to <see cref="WrapperToolMode.Lazy"/> on both hosts: the tool list stays minimal
    /// per session and wrappers are callable by name. Set <see cref="WrapperToolMode.Eager"/> for
    /// clients that refuse to call a tool they have not listed.
    /// </summary>
    public WrapperToolMode WrapperMode { get; set; } = WrapperToolMode.Lazy;

    public string SelfName { get; set; } = "mcp-aggregator";
    public string SelfDescription { get; set; } = "MCP Aggregator gateway — proxies tool calls to multiple downstream MCP servers. IMPORTANT: call get_service_skill(serverName: \"mcp-aggregator\") first to get the full usage guide.";
    public string SelfApiDescription { get; set; } = "MCP Aggregator gateway — proxies tool calls to multiple downstream MCP servers.";

    public string RegistryFilePath => Path.Combine(DataDirectory, RegistryFile);
    public string SkillsDirectoryPath => Path.Combine(DataDirectory, SkillsDirectory);
}
