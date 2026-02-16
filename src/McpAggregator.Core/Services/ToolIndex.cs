using System.Collections.Concurrent;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public class ToolIndex
{
    private readonly ServerRegistry _registry;
    private readonly ConnectionManager _connectionManager;
    private readonly AggregatorOptions _options;
    private readonly ILogger<ToolIndex> _logger;

    private readonly ConcurrentDictionary<string, CachedTools> _cache = new(StringComparer.OrdinalIgnoreCase);

    private record CachedTools(List<ToolDetail> Tools, DateTimeOffset FetchedAt);

    public ToolIndex(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        IOptions<AggregatorOptions> options,
        ILogger<ToolIndex> logger)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;

        _registry.RegistryChanged += () =>
        {
            // Invalidate cache for removed servers
            var registered = _registry.GetAll().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = _cache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in stale) _cache.TryRemove(key, out _);
        };
    }

    public async Task<List<ServiceIndex>> GetIndexAsync(CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var servers = _registry.GetAll();
        var results = new List<ServiceIndex>();

        foreach (var server in servers)
        {
            var index = new ServiceIndex
            {
                Name = server.Name,
                DisplayName = server.DisplayName,
                Description = server.Description,
                Enabled = server.Enabled,
                HasSkillDocument = server.HasSkillDocument
            };

            if (server.Enabled)
            {
                try
                {
                    var tools = await GetToolsForServerAsync(server.Name, ct);
                    index.Available = true;
                    index.Tools = tools.Select(t => new ToolSummary
                    {
                        Name = t.Name,
                        Description = t.Description
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get tools for '{Server}'", server.Name);
                    index.Available = false;
                }
            }

            results.Add(index);
        }

        return results;
    }

    public async Task<ServiceDetails> GetDetailsAsync(string serverName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var tools = await GetToolsForServerAsync(serverName, ct);

        return new ServiceDetails
        {
            Name = server.Name,
            DisplayName = server.DisplayName,
            Description = server.Description,
            Enabled = server.Enabled,
            Available = true,
            HasSkillDocument = server.HasSkillDocument,
            Tools = tools
        };
    }

    public async Task<List<ToolDetail>> GetToolsForServerAsync(string serverName, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(serverName, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _options.IndexCacheTtl)
        {
            return cached.Tools;
        }

        var client = await _connectionManager.GetClientAsync(serverName, ct);
        var mcpTools = await client.ListToolsAsync(cancellationToken: ct);

        var tools = mcpTools.Select(t => new ToolDetail
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.JsonSchema is { } schema
                ? JsonSerializer.Deserialize<object>(schema.GetRawText())
                : null
        }).ToList();

        _cache[serverName] = new CachedTools(tools, DateTimeOffset.UtcNow);
        _logger.LogDebug("Cached {Count} tools for '{Server}'", tools.Count, serverName);
        return tools;
    }

    public void InvalidateCache(string? serverName = null)
    {
        if (serverName is not null)
            _cache.TryRemove(serverName, out _);
        else
            _cache.Clear();
    }
}
