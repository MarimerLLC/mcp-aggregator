using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public class SkillStore
{
    private readonly string _skillsDir;
    private readonly ILogger<SkillStore> _logger;
    private const int MaxSkillSizeBytes = 256 * 1024; // 256KB

    public SkillStore(IOptions<AggregatorOptions> options, ILogger<SkillStore> logger)
    {
        _skillsDir = options.Value.SkillsDirectoryPath;
        _logger = logger;
        Directory.CreateDirectory(_skillsDir);
    }

    public async Task<string?> GetAsync(string serverName, CancellationToken ct = default)
    {
        var path = GetPath(serverName);
        if (!File.Exists(path))
            return null;

        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task SetAsync(string serverName, string markdown, CancellationToken ct = default)
    {
        if (markdown.Length > MaxSkillSizeBytes)
            throw new AggregatorException($"Skill document exceeds maximum size of {MaxSkillSizeBytes} bytes.");

        var path = GetPath(serverName);
        await File.WriteAllTextAsync(path, markdown, ct);
        _logger.LogInformation("Saved skill document for '{Server}'", serverName);
    }

    public bool Delete(string serverName)
    {
        var path = GetPath(serverName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Deleted skill document for '{Server}'", serverName);
        return true;
    }

    public bool Exists(string serverName) => File.Exists(GetPath(serverName));

    private string GetPath(string serverName) =>
        Path.Combine(_skillsDir, $"{serverName}.md");
}
