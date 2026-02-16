namespace McpAggregator.Core.Models;

public class RegistryData
{
    public string Version { get; set; } = "1.0";
    public List<RegisteredServer> Servers { get; set; } = [];
}
