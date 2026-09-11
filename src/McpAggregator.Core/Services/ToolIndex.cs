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
    private readonly AdminToolSet? _adminTools;
    private readonly ILogger<ToolIndex> _logger;

    private readonly ConcurrentDictionary<string, CachedTools> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedPrompts> _promptCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedResources> _resourceCache = new(StringComparer.OrdinalIgnoreCase);

    private record CachedTools(List<ToolDetail> Tools, DateTimeOffset FetchedAt, string Fingerprint);
    private record CachedPrompts(List<PromptDetail> Prompts, DateTimeOffset FetchedAt, string Fingerprint);
    private record CachedResources(List<ResourceDetail> Resources, DateTimeOffset FetchedAt, string Fingerprint);

    /// <summary>
    /// Raised with a server name whenever that server's indexed tool set may have changed: an
    /// explicit <see cref="InvalidateCache"/>, or a TTL-driven re-fetch that came back with a
    /// different set of tool names or schemas. <see cref="WrapperToolCatalog"/> listens to keep the
    /// typed wrapper tools in step. Handlers run synchronously on the caller's thread.
    /// </summary>
    public event Action<string>? ToolsChanged;

    /// <summary>
    /// The prompt counterpart of <see cref="ToolsChanged"/>: raised when a server's indexed prompt
    /// set may have changed (explicit invalidation, or a TTL re-fetch that returned different
    /// prompt names or arguments). <see cref="WrapperToolCatalog"/> listens to keep the proxied
    /// prompts in step.
    /// </summary>
    public event Action<string>? PromptsChanged;

    /// <summary>
    /// The resource counterpart of <see cref="ToolsChanged"/>: raised when a server's indexed
    /// resource or resource-template set may have changed (explicit invalidation, or a TTL re-fetch
    /// that returned different URIs, names, titles, descriptions, MIME types or sizes).
    /// <see cref="WrapperToolCatalog"/> listens to keep the bridged resources in step (issue #45).
    /// </summary>
    public event Action<string>? ResourcesChanged;

    public ToolIndex(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        IOptions<AggregatorOptions> options,
        ILogger<ToolIndex> logger,
        IOptions<McpServerOptions>? mcpServerOptions = null,
        AdminToolSet? adminTools = null)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _skillStore = skillStore;
        _options = options.Value;
        _mcpServerOptions = mcpServerOptions;
        _adminTools = adminTools;
        _logger = logger;

        _registry.RegistryChanged += () =>
        {
            // Invalidate cache for removed servers
            var registered = _registry.GetAll().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = _cache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in stale) _cache.TryRemove(key, out _);
            var stalePrompts = _promptCache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in stalePrompts) _promptCache.TryRemove(key, out _);
            var staleResources = _resourceCache.Keys.Where(k => !registered.Contains(k)).ToList();
            foreach (var key in staleResources) _resourceCache.TryRemove(key, out _);
        };
    }

    public async Task<List<ServiceIndex>> GetIndexAsync(CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var servers = _registry.GetAll();
        var results = new List<ServiceIndex>();

        // Advertise the aggregator itself if it has a skill document
        if (_skillStore.Exists(_options.SelfName))
            results.Add(SelfIndex());

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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // "No prompt support" is already an empty list from GetPromptsForServerAsync;
                        // anything that throws is a real failure. Leave prompts null so freshness
                        // reads "unknown" rather than a false "stale" against an empty prompt set.
                        _logger.LogDebug(ex, "Failed to get prompts for '{Server}'; skill freshness is unknown", server.Name);
                        prompts = null;
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

    /// <summary>
    /// <c>null</c> without a skill document; <c>unknown</c> when no baseline was recorded or the
    /// current tools or prompts could not be read; <c>stale</c> when the recorded version (if one
    /// was recorded) differs from the server's current version or the recorded fingerprint no longer
    /// matches (see <see cref="SkillFingerprint"/>); otherwise <c>fresh</c>.
    /// </summary>
    private static string? ComputeFreshness(
        RegisteredServer server,
        IReadOnlyList<ToolDetail>? currentTools,
        IReadOnlyList<PromptDetail>? currentPrompts)
    {
        if (!server.HasSkillDocument)
            return null;

        if (string.IsNullOrEmpty(server.SkillRecordedFingerprint))
            return "unknown";

        if (currentTools is null || currentPrompts is null)
            return "unknown";

        if (server.SkillRecordedVersion is not null
            && !string.Equals(server.SkillRecordedVersion, server.RemoteVersion, StringComparison.Ordinal))
        {
            return "stale";
        }

        return SkillFingerprint.Matches(server.SkillRecordedFingerprint, currentTools, currentPrompts)
            ? "fresh"
            : "stale";
    }

    /// <summary><see cref="ServiceIndex.Id"/> of the aggregator's own entry; real downstreams carry hex ids.</summary>
    public const string SelfId = "self";

    private const string SelfNote =
        " This entry is the aggregator itself, not a downstream: its tools are ordinary tools on this connection, " +
        "called directly by name and never through invoke_tool.";

    /// <summary>
    /// The aggregator's own tools: everything in the shared collection that is not a downstream
    /// wrapper, plus the administrative tools (absent from the collection in Lazy mode). Each
    /// tool's <c>wrapperName</c> is its own name, since it is called directly.
    /// </summary>
    private List<ToolDetail> OwnTools()
    {
        var tools = new List<ToolDetail>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var collection = _mcpServerOptions?.Value.ToolCollection;
        if (collection is not null)
        {
            foreach (var tool in collection)
            {
                if (tool is DownstreamToolWrapper || !seen.Add(tool.ProtocolTool.Name))
                    continue;
                tools.Add(new ToolDetail
                {
                    Name = tool.ProtocolTool.Name,
                    Description = tool.ProtocolTool.Description,
                    InputSchema = tool.ProtocolTool.InputSchema,
                    WrapperName = tool.ProtocolTool.Name,
                });
            }
        }

        // AdminToolSet carries the built tools with their input schemas; the reflection-only
        // Describe() is the fallback when the index is hosted without the MCP server (REST-only
        // tests), where a schema-less entry is better than none.
        if (_adminTools is not null)
        {
            foreach (var tool in _adminTools.Tools)
            {
                if (seen.Add(tool.ProtocolTool.Name))
                    tools.Add(new ToolDetail
                    {
                        Name = tool.ProtocolTool.Name,
                        Description = tool.ProtocolTool.Description,
                        InputSchema = tool.ProtocolTool.InputSchema,
                        WrapperName = tool.ProtocolTool.Name,
                    });
            }
        }
        else
        {
            foreach (var (name, description) in AdminTools.Describe())
            {
                if (seen.Add(name))
                    tools.Add(new ToolDetail { Name = name, Description = description, WrapperName = name });
            }
        }

        return tools;
    }

    private ServiceIndex SelfIndex()
    {
        var selfInfo = _mcpServerOptions?.Value.ServerInfo;
        return new ServiceIndex
        {
            Id = SelfId,
            Name = _options.SelfName,
            DisplayName = "MCP Aggregator",
            Description = _options.SelfDescription + SelfNote,
            Enabled = true,
            Available = true,
            HasSkillDocument = true,
            RemoteName = selfInfo?.Name,
            RemoteTitle = selfInfo?.Title,
            RemoteVersion = selfInfo?.Version,
            Tools = OwnTools().Select(t => new ToolSummary { Name = t.Name, Description = t.Description, WrapperName = t.WrapperName }).ToList(),
        };
    }

    public async Task<ServiceDetails> GetDetailsAsync(string serverName, CancellationToken ct = default)
    {
        if (string.Equals(serverName, _options.SelfName, StringComparison.OrdinalIgnoreCase))
        {
            var self = SelfIndex();
            return new ServiceDetails
            {
                Id = self.Id,
                Name = self.Name,
                DisplayName = self.DisplayName,
                Description = self.Description,
                Enabled = true,
                Available = true,
                HasSkillDocument = _skillStore.Exists(_options.SelfName),
                RemoteName = self.RemoteName,
                RemoteTitle = self.RemoteTitle,
                RemoteVersion = self.RemoteVersion,
                Tools = OwnTools(),
            };
        }

        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var tools = await GetToolsForServerAsync(serverName, ct);

        // Null (not empty) on failure so freshness reads "unknown"; the DTO list falls back to empty.
        List<PromptDetail>? prompts = null;
        try
        {
            prompts = await GetPromptsForServerAsync(serverName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to get prompts for '{Server}'; skill freshness is unknown", serverName);
        }

        List<ResourceDetail> resources = [];
        try
        {
            resources = await GetResourcesForServerAsync(serverName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server '{Server}' does not expose resources", serverName);
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
            Prompts = prompts ?? [],
            Resources = resources
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
            StorePrompts(serverName, none);
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
            }).ToList() ?? [],
            WrapperName = WrapperNaming.For(serverName, p.Name),
            Protocol = p.ProtocolPrompt
        }).ToList();

        StorePrompts(serverName, prompts);
        _logger.LogDebug("Cached {Count} prompts for '{Server}'", prompts.Count, serverName);
        return prompts;
    }

    private void StorePrompts(string serverName, List<PromptDetail> prompts)
    {
        var fingerprint = ComputePromptFingerprint(prompts);
        var replaced = _promptCache.TryGetValue(serverName, out var previous)
            && !string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal);

        _promptCache[serverName] = new CachedPrompts(prompts, DateTimeOffset.UtcNow, fingerprint);

        if (replaced)
        {
            _logger.LogInformation("Prompt set for '{Server}' changed on refresh", serverName);
            RaisePromptsChanged(serverName);
        }
    }

    /// <summary>
    /// Name, title, description and serialized arguments per prompt, in name order. Unlike tools,
    /// a prompt's description is part of what the host shows the user, so it counts.
    /// </summary>
    private static string ComputePromptFingerprint(IEnumerable<PromptDetail> prompts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in prompts.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append(p.Name).Append('');
            sb.Append(p.Protocol?.Title ?? string.Empty).Append('');
            sb.Append(p.Protocol?.Description ?? p.Description ?? string.Empty).Append('');
            if (p.Protocol?.Arguments is { } args)
                sb.Append(JsonSerializer.Serialize(args, McpJsonUtilities.DefaultOptions));
            sb.Append('');
        }
        return sb.ToString();
    }

    private void RaisePromptsChanged(string serverName)
    {
        try
        {
            PromptsChanged?.Invoke(serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A PromptsChanged handler failed for '{Server}'", serverName);
        }
    }

    // ---------------------------------------------------------------- resources (issue #45)

    /// <summary>
    /// The server's resources and resource templates, cached like tools and prompts. A server that
    /// does not implement <c>resources/list</c> has no resources (an empty list is cached, not a
    /// fault); one that lists resources but not templates yields resources only. Each entry carries
    /// the wire object so <see cref="DownstreamResourceWrapper"/> can carry everything through.
    /// </summary>
    public async Task<List<ResourceDetail>> GetResourcesForServerAsync(string serverName, CancellationToken ct = default)
    {
        if (_resourceCache.TryGetValue(serverName, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _options.IndexCacheTtl)
        {
            return cached.Resources;
        }

        IList<McpClientResource> mcpResources;
        try
        {
            mcpResources = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientResource>>(serverName,
                async (client, token) => await client.ListResourcesAsync(cancellationToken: token), ct);
        }
        catch (McpProtocolException ex) when (ConnectionManager.IsUnsupportedCapability(ex))
        {
            _logger.LogDebug(ex, "Server '{Server}' does not support resources/list; indexing zero resources", serverName);
            List<ResourceDetail> none = [];
            StoreResources(serverName, none);
            return none;
        }

        IList<McpClientResourceTemplate> mcpTemplates;
        try
        {
            mcpTemplates = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientResourceTemplate>>(serverName,
                async (client, token) => await client.ListResourceTemplatesAsync(cancellationToken: token), ct);
        }
        catch (McpProtocolException ex) when (ConnectionManager.IsUnsupportedCapability(ex))
        {
            // resources/list worked, so the server has resource support; templates are simply
            // something it does not do.
            _logger.LogDebug(ex, "Server '{Server}' does not support resources/templates/list", serverName);
            mcpTemplates = [];
        }

        var resources = mcpResources.Select(r => new ResourceDetail
        {
            Name = r.Name,
            Title = r.Title,
            Description = r.Description,
            MimeType = r.MimeType,
            Size = r.ProtocolResource.Size,
            IsTemplate = false,
            DownstreamUri = r.Uri,
            Uri = ResourceUriNaming.For(serverName, r.Uri),
            Protocol = r.ProtocolResource
        }).Concat(mcpTemplates.Select(t => new ResourceDetail
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            MimeType = t.MimeType,
            IsTemplate = true,
            DownstreamUri = t.UriTemplate,
            Uri = ResourceUriNaming.For(serverName, t.UriTemplate),
            ProtocolTemplate = t.ProtocolResourceTemplate
        })).ToList();

        StoreResources(serverName, resources);
        _logger.LogDebug("Cached {Count} resource(s) for '{Server}'", resources.Count, serverName);
        return resources;
    }

    private void StoreResources(string serverName, List<ResourceDetail> resources)
    {
        var fingerprint = ComputeResourceFingerprint(resources);
        var replaced = _resourceCache.TryGetValue(serverName, out var previous)
            && !string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal);

        _resourceCache[serverName] = new CachedResources(resources, DateTimeOffset.UtcNow, fingerprint);

        if (replaced)
        {
            _logger.LogInformation("Resource set for '{Server}' changed on refresh", serverName);
            RaiseResourcesChanged(serverName);
        }
    }

    /// <summary>
    /// URI (or template), name, title, description, MIME type and size per resource, in URI order.
    /// All of it is what a host shows the user, so all of it counts.
    /// </summary>
    private static string ComputeResourceFingerprint(IEnumerable<ResourceDetail> resources)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in resources.OrderBy(r => r.DownstreamUri, StringComparer.Ordinal))
        {
            sb.Append(r.IsTemplate ? 'T' : 'R').Append('');
            sb.Append(r.DownstreamUri).Append('');
            sb.Append(r.Name).Append('');
            sb.Append(r.Title ?? string.Empty).Append('');
            sb.Append(r.Description ?? string.Empty).Append('');
            sb.Append(r.MimeType ?? string.Empty).Append('');
            sb.Append(r.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append('');
        }
        return sb.ToString();
    }

    private void RaiseResourcesChanged(string serverName)
    {
        try
        {
            ResourcesChanged?.Invoke(serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A ResourcesChanged handler failed for '{Server}'", serverName);
        }
    }

    public void InvalidateCache(string? serverName = null)
    {
        if (serverName is not null)
        {
            _cache.TryRemove(serverName, out _);
            _promptCache.TryRemove(serverName, out _);
            _resourceCache.TryRemove(serverName, out _);
            RaiseToolsChanged(serverName);
            RaisePromptsChanged(serverName);
            RaiseResourcesChanged(serverName);
        }
        else
        {
            var affected = _cache.Keys.ToList();
            var affectedPrompts = _promptCache.Keys.ToList();
            var affectedResources = _resourceCache.Keys.ToList();
            _cache.Clear();
            _promptCache.Clear();
            _resourceCache.Clear();
            foreach (var name in affected)
                RaiseToolsChanged(name);
            foreach (var name in affectedPrompts)
                RaisePromptsChanged(name);
            foreach (var name in affectedResources)
                RaiseResourcesChanged(name);
        }
    }
}
