using McpAggregator.Core.Configuration;
using System.Text.Json;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
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
        DeleteBaseline(serverName);
        var path = GetPath(serverName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Deleted skill document for '{Server}'", serverName);
        return true;
    }

    public bool Exists(string serverName) => File.Exists(GetPath(serverName));

    /// <summary>
    /// The catalog snapshot recorded with the skill (issue #56), or null when none was recorded
    /// (skills saved before #56, or a capture that could not read the server). An unreadable file is
    /// treated as absent and logged, so freshness falls back to the fingerprint check.
    /// </summary>
    public async Task<SkillBaseline?> GetBaselineAsync(string serverName, CancellationToken ct = default)
    {
        var path = GetBaselinePath(serverName);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SkillBaseline>(stream, BaselineJson, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable skill baseline for '{Server}'", serverName);
            return null;
        }
    }

    /// <summary>
    /// Writes the baseline atomically (temp file + rename), like the registry. Writes to one
    /// baseline are serialized (on Windows two concurrent renames onto the same file fail), and each
    /// stages in its own temp file, so two saves of the same skill never touch each other's file;
    /// the last one wins.
    /// </summary>
    public async Task SetBaselineAsync(string serverName, SkillBaseline baseline, CancellationToken ct = default)
    {
        var path = GetBaselinePath(serverName);
        var gate = BaselineWriteLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, baseline, BaselineJson, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
            gate.Release();
        }
    }

    /// <summary>Keyed by full path so every <see cref="SkillStore"/> on the same directory shares them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> BaselineWriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public bool DeleteBaseline(string serverName)
    {
        var path = GetBaselinePath(serverName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static readonly JsonSerializerOptions BaselineJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private string GetPath(string serverName) =>
        Path.Combine(_skillsDir, $"{serverName}.md");

    private string GetBaselinePath(string serverName) =>
        Path.Combine(_skillsDir, $"{serverName}.baseline.json");
}
