using System.Collections.Concurrent;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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

    private record CachedTools(List<ToolDetail> Tools, DateTimeOffset FetchedAt, string Fingerprint);
    private record CachedPrompts(List<PromptDetail> Prompts, DateTimeOffset FetchedAt);

    /// <summary>
    /// Raised with a server name whenever that server's indexed tool set may have changed: an
    /// explicit <see cref="InvalidateCache"/>, or a TTL-driven re-fetch that came back with a
    /// different set of tool names or schemas. <see cref="WrapperToolCatalog"/> listens to keep the
    /// typed wrapper tools in step. Handlers run synchronously on the caller's thread.
    /// </summary>
    public event Action<string>? ToolsChanged;

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
                Id = server.Id,
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
                        Description = t.Description,
                        WrapperName = t.WrapperName
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
            Id = server.Id,
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

        IList<McpClientTool> mcpTools;
        try
        {
            mcpTools = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(serverName,
                async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);
        }
        catch (JsonException ex)
        {
            // MCP 2026-07-28 makes Tool.inputSchema required, so the SDK now throws instead of
            // silently defaulting the schema. A downstream server that omits it is non-conformant;
            // name it explicitly rather than surfacing a bare deserialization error.
            _logger.LogWarning(ex, "Server '{Server}' returned a malformed tools/list payload", serverName);
            throw new AggregatorException(
                $"Server '{serverName}' returned a tools/list payload the MCP 2026-07-28 schema rejects " +
                $"(every tool must declare an 'inputSchema'; an empty object is sufficient). " +
                $"Underlying error: {ex.Message}", ex);
        }

        var tools = mcpTools.Select(t => new ToolDetail
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.JsonSchema is { } schema
                ? JsonSerializer.Deserialize<object>(schema.GetRawText())
                : null,
            WrapperName = WrapperNaming.For(serverName, t.Name),
            Protocol = t.ProtocolTool
        }).ToList();

        var fingerprint = ComputeToolFingerprint(tools);
        var replaced = _cache.TryGetValue(serverName, out var previous)
            && !string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal);

        _cache[serverName] = new CachedTools(tools, DateTimeOffset.UtcNow, fingerprint);
        _logger.LogDebug("Cached {Count} tools for '{Server}'", tools.Count, serverName);

        if (replaced)
        {
            // A TTL refresh found a different tool set (or schema) than the one the wrappers were
            // built from; tell the catalog so the typed wrappers are rebuilt.
            _logger.LogInformation("Tool set for '{Server}' changed on refresh", serverName);
            RaiseToolsChanged(serverName);
        }

        return tools;
    }

    /// <summary>
    /// Name plus raw input-schema text per tool, in name order. Detects a renamed tool or a schema
    /// change between refreshes; description-only changes do not count because they do not affect
    /// how a wrapper is called.
    /// </summary>
    private static string ComputeToolFingerprint(IEnumerable<ToolDetail> tools)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(t.Name).Append('\u0001');
            sb.Append(t.Protocol?.InputSchema.GetRawText() ?? string.Empty).Append('\u0002');
        }
        return sb.ToString();
    }

    private void RaiseToolsChanged(string serverName)
    {
        try
        {
            ToolsChanged?.Invoke(serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A ToolsChanged handler failed for '{Server}'", serverName);
        }
    }

    public async Task<List<PromptDetail>> GetPromptsForServerAsync(string serverName, CancellationToken ct = default)
    {
        if (_promptCache.TryGetValue(serverName, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _options.IndexCacheTtl)
        {
            return cached.Prompts;
        }

        IList<McpClientPrompt> mcpPrompts;
        try
        {
            mcpPrompts = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(serverName,
                async (client, token) => await client.ListPromptsAsync(cancellationToken: token), ct);
        }
        catch (McpProtocolException ex) when (ConnectionManager.IsUnsupportedCapability(ex))
        {
            // Prompts are an optional capability. A server that does not implement prompts/list
            // has no prompts, which is a fact about the server rather than a failure to index it —
            // AdminTools and AdminController already treat prompt listing as best-effort, and this
            // is the same contract on the cached path. Cache the empty result so an unsupported
            // call is not re-issued on every service-details fetch.
            _logger.LogDebug(ex, "Server '{Server}' does not support prompts/list; indexing zero prompts", serverName);
            List<PromptDetail> none = [];
            _promptCache[serverName] = new CachedPrompts(none, DateTimeOffset.UtcNow);
            return none;
        }

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
            RaiseToolsChanged(serverName);
        }
        else
        {
            var affected = _cache.Keys.ToList();
            _cache.Clear();
            _promptCache.Clear();
            foreach (var name in affected)
                RaiseToolsChanged(name);
        }
    }
}
