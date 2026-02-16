using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Storage;

public class JsonFilePersistence : IRegistryPersistence
{
    private readonly string _filePath;
    private readonly ILogger<JsonFilePersistence> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JsonFilePersistence(IOptions<AggregatorOptions> options, ILogger<JsonFilePersistence> logger)
    {
        _filePath = options.Value.RegistryFilePath;
        _logger = logger;
    }

    public async Task<RegistryData> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("Registry file not found at {Path}, starting with empty registry", _filePath);
            return new RegistryData();
        }

        await _lock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<RegistryData>(json, JsonOptions) ?? new RegistryData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load registry from {Path}", _filePath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(RegistryData data, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await _lock.WaitAsync(ct);
        try
        {
            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogDebug("Registry saved to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save registry to {Path}", _filePath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
