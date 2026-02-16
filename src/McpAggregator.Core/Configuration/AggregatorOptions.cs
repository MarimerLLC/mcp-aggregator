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

    public string RegistryFilePath => Path.Combine(DataDirectory, RegistryFile);
    public string SkillsDirectoryPath => Path.Combine(DataDirectory, SkillsDirectory);
}
