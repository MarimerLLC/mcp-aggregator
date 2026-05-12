using System.Collections.Concurrent;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Services;

public class ToolIndex
{
    private readonly ServerRegistry _registry;
    private readonly ConnectionManager _connectionManager;
    private readonly SkillStore _skillStore;
    private readonly AggregatorOptions _options;
    private readonly IOptions<McpServerOptions>? _mcpServerOptions;
    private readonly ILogger<ToolIndex> _logger;

    private readonly ConcurrentDictionary<string, CachedTools> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedPrompts> _promptCache = new(StringComparer.OrdinalIgnoreCase);

    private record CachedTools(List<ToolDetail> Tools, DateTimeOffset FetchedAt);
    private record CachedPrompts(List<PromptDetail> Prompts, DateTimeOffset FetchedAt);

    public ToolIndex(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        IOptions<AggregatorOptions> options,
        ILogger<ToolIndex> logger,
        IOptions<McpServerOptions>? mcpServerOptions = null)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _skillStore = skillStore;
        _options = options.Value;
        _mcpServerOptions = mcpServerOptions;
        _logger = logger;

        _registry.RegistryChanged += () =>
        {
            // Invalidate cache for removed servers
            var registered = _registry.GetAll().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = _cache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in stale) _cache.TryRemove(key, out _);
            var stalePrompts = _promptCache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in stalePrompts) _promptCache.TryRemove(key, out _);
        };
    }

    public async Task<List<ServiceIndex>> GetIndexAsync(CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var servers = _registry.GetAll();
        var results = new List<ServiceIndex>();

        // Advertise the aggregator itself if it has a skill document
        if (_skillStore.Exists(_options.SelfName))
        {
            var selfInfo = _mcpServerOptions?.Value.ServerInfo;
            results.Add(new ServiceIndex
            {
                Name = _options.SelfName,
                DisplayName = "MCP Aggregator",
                Description = _options.SelfDescription,
                Enabled = true,
                Available = true,
                HasSkillDocument = true,
                RemoteName = selfInfo?.Name,
                RemoteTitle = selfInfo?.Title,
                RemoteVersion = selfInfo?.Version
            });
        }

        foreach (var server in servers)
        {
            var index = new ServiceIndex
            {
                Name = server.Name,
                DisplayName = server.DisplayName,
                Description = server.AiSummary ?? server.Description,
                Enabled = server.Enabled,
                HasSkillDocument = server.HasSkillDocument,
                SkillRecordedVersion = server.SkillRecordedVersion,
                SkillRecordedAt = server.SkillRecordedAt
            };

            List<ToolDetail>? tools = null;
            List<PromptDetail>? prompts = null;

            if (server.Enabled)
            {
                try
                {
                    tools = await GetToolsForServerAsync(server.Name, ct);
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

                if (index.Available && server.HasSkillDocument)
                {
                    try
                    {
                        prompts = await GetPromptsForServerAsync(server.Name, ct);
                    }
                    catch
                    {
                        prompts = [];
                    }
                }
            }

            // Populate after the tools call so a fresh connect has refreshed the cached metadata.
            index.RemoteName = server.RemoteName;
            index.RemoteTitle = server.RemoteTitle;
            index.RemoteVersion = server.RemoteVersion;
            index.SkillFreshness = ComputeFreshness(server, tools, prompts);

            results.Add(index);
        }

        return results;
    }

    private static string? ComputeFreshness(
        RegisteredServer server,
        IReadOnlyList<ToolDetail>? currentTools,
        IReadOnlyList<PromptDetail>? currentPrompts)
    {
        if (!server.HasSkillDocument)
            return null;

        if (string.IsNullOrEmpty(server.SkillRecordedFingerprint))
            return "unknown";

        if (currentTools is null)
            return "unknown";

        var currentFingerprint = SkillFingerprint.Compute(
            currentTools.Select(t => t.Name),
            currentPrompts?.Select(p => p.Name) ?? []);

        return string.Equals(currentFingerprint, server.SkillRecordedFingerprint, StringComparison.Ordinal)
            ? "fresh"
            : "stale";
    }

    public async Task<ServiceDetails> GetDetailsAsync(string serverName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var tools = await GetToolsForServerAsync(serverName, ct);

        List<PromptDetail> prompts = [];
        try
        {
            prompts = await GetPromptsForServerAsync(serverName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server '{Server}' does not expose prompts", serverName);
        }

        return new ServiceDetails
        {
            Name = server.Name,
            DisplayName = server.DisplayName,
            Description = server.Description,
            Enabled = server.Enabled,
            Available = true,
            HasSkillDocument = server.HasSkillDocument,
            RemoteName = server.RemoteName,
            RemoteTitle = server.RemoteTitle,
            RemoteVersion = server.RemoteVersion,
            RemoteInstructions = server.RemoteInstructions,
            SkillFreshness = ComputeFreshness(server, tools, prompts),
            SkillRecordedVersion = server.SkillRecordedVersion,
            SkillRecordedAt = server.SkillRecordedAt,
            Tools = tools,
            Prompts = prompts
        };
    }

    public async Task<List<ToolDetail>> GetToolsForServerAsync(string serverName, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(serverName, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _options.IndexCacheTtl)
        {
            return cached.Tools;
        }

        var mcpTools = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(serverName,
            async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);

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

    public async Task<List<PromptDetail>> GetPromptsForServerAsync(string serverName, CancellationToken ct = default)
    {
        if (_promptCache.TryGetValue(serverName, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _options.IndexCacheTtl)
        {
            return cached.Prompts;
        }

        var mcpPrompts = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(serverName,
            async (client, token) => await client.ListPromptsAsync(cancellationToken: token), ct);

        var prompts = mcpPrompts.Select(p => new PromptDetail
        {
            Name = p.Name,
            Description = p.Description,
            Arguments = p.ProtocolPrompt.Arguments?.Select(a => new PromptArgumentDetail
            {
                Name = a.Name,
                Description = a.Description,
                Required = a.Required ?? false
            }).ToList() ?? []
        }).ToList();

        _promptCache[serverName] = new CachedPrompts(prompts, DateTimeOffset.UtcNow);
        _logger.LogDebug("Cached {Count} prompts for '{Server}'", prompts.Count, serverName);
        return prompts;
    }

    public void InvalidateCache(string? serverName = null)
    {
        if (serverName is not null)
        {
            _cache.TryRemove(serverName, out _);
            _promptCache.TryRemove(serverName, out _);
        }
        else
        {
            _cache.Clear();
            _promptCache.Clear();
        }
    }
}
