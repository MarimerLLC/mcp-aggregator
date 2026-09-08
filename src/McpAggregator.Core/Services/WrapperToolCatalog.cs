using System.Collections.Concurrent;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Services;

/// <summary>One <c>find_tools</c> hit.</summary>
public sealed record FindToolsMatch(DownstreamToolWrapper Wrapper, RegisteredServer Server, ToolDetail Detail, int Score);

/// <summary>Result of <see cref="WrapperToolCatalog.FindAsync"/>.</summary>
/// <param name="Matches">Best matches first.</param>
/// <param name="SkippedServers">Enabled servers whose tools could not be listed, with the reason.</param>
public sealed record FindToolsResult(IReadOnlyList<FindToolsMatch> Matches, IReadOnlyList<string> SkippedServers);

/// <summary>
/// Owns the typed wrapper tools (<c>{server}__{tool}</c>) the aggregator exposes for downstream
/// tools and keeps <see cref="McpServerOptions.ToolCollection"/> in step with the registry and the
/// tool index. The SDK's server subscribes to the collection's <c>Changed</c> event and sends
/// <c>notifications/tools/list_changed</c> itself, so this class never talks to a session.
/// <para>
/// Invariant: the catalog only ever adds or removes <see cref="DownstreamToolWrapper"/> instances.
/// The aggregator's own attributed tools are never touched.
/// </para>
/// </summary>
public sealed class WrapperToolCatalog
{
    private readonly ServerRegistry _registry;
    private readonly ToolIndex _toolIndex;
    private readonly ToolProxyHandler _proxy;
    private readonly AggregatorOptions _options;
    private readonly IOptions<McpServerOptions> _mcpServerOptions;
    private readonly ILogger<WrapperToolCatalog> _logger;

    // Last built wrapper set per server, keyed by server name.
    private readonly ConcurrentDictionary<string, IReadOnlyList<DownstreamToolWrapper>> _built = new(StringComparer.OrdinalIgnoreCase);

    // Lazy mode only: wrapper name → owning server name.
    private readonly ConcurrentDictionary<string, string> _activated = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _warnedLongNames = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private int _syncQueued;
    private volatile Task _scheduledSync = Task.CompletedTask;

    public WrapperToolCatalog(
        ServerRegistry registry,
        ToolIndex toolIndex,
        ToolProxyHandler proxy,
        IOptions<AggregatorOptions> options,
        IOptions<McpServerOptions> mcpServerOptions,
        ILogger<WrapperToolCatalog> logger)
    {
        _registry = registry;
        _toolIndex = toolIndex;
        _proxy = proxy;
        _options = options.Value;
        _mcpServerOptions = mcpServerOptions;
        _logger = logger;

        _registry.RegistryChanged += OnRegistryChanged;
        _toolIndex.ToolsChanged += OnToolsChanged;
    }

    public WrapperToolMode Mode => _options.WrapperMode;

    /// <summary>The wrapper tools currently exposed in the aggregator's tool list.</summary>
    public IReadOnlyList<DownstreamToolWrapper> ActiveWrappers
        => ToolCollection.OfType<DownstreamToolWrapper>().ToList();

    /// <summary>
    /// The sync most recently scheduled by a registry or index event. Tests await this to observe
    /// the effect of a fire-and-forget resync; production code never needs it.
    /// </summary>
    public Task PendingSync => _scheduledSync;

    private McpServerPrimitiveCollection<McpServerTool> ToolCollection
    {
        get
        {
            var options = _mcpServerOptions.Value;
            // AddAggregatorMcpServer post-configures this to non-null; the fallback only matters
            // for a catalog constructed outside that wiring.
            return options.ToolCollection ??= [];
        }
    }

    /// <summary>
    /// Builds (or reuses) the wrapper set for one server from the tool index. A wrapper instance is
    /// reused when its name and schema fingerprint are unchanged so the tool collection is not
    /// churned by a refresh that changed nothing.
    /// </summary>
    public async Task<IReadOnlyList<DownstreamToolWrapper>> GetWrappersAsync(string serverName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var tools = await _toolIndex.GetToolsForServerAsync(server.Name, ct);

        var previous = _built.TryGetValue(server.Name, out var existing)
            ? existing.ToDictionary(w => w.ProtocolTool.Name, StringComparer.Ordinal)
            : new Dictionary<string, DownstreamToolWrapper>(StringComparer.Ordinal);

        var wrappers = new List<DownstreamToolWrapper>(tools.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (tool.Protocol is null)
                continue;

            var name = WrapperNaming.For(server.Name, tool.Name);
            if (!seen.Add(name))
            {
                _logger.LogWarning(
                    "Two tools on '{Server}' map to the same wrapper name '{Wrapper}' after sanitization; keeping the first",
                    server.Name, name);
                continue;
            }

            var fingerprint = DownstreamToolWrapper.Fingerprint(tool.Protocol.InputSchema);
            if (previous.TryGetValue(name, out var reusable)
                && string.Equals(reusable.SchemaFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(reusable.ServerId, server.Id, StringComparison.Ordinal))
            {
                wrappers.Add(reusable);
                continue;
            }

            if (name.Length > WrapperNaming.HostNameLengthLimit && _warnedLongNames.TryAdd(name, 0))
            {
                _logger.LogWarning(
                    "Wrapper tool name '{Wrapper}' is {Length} characters; hosts that cap tool names at {Limit} may drop it",
                    name, name.Length, WrapperNaming.HostNameLengthLimit);
            }

            wrappers.Add(new DownstreamToolWrapper(server, tool.Protocol, _proxy, _logger));
        }

        _built[server.Name] = wrappers;
        return wrappers;
    }

    /// <summary>
    /// Searches every enabled server's tools for <paramref name="query"/>. An exact tool or wrapper
    /// name match ranks first, then token hits on the tool name, wrapper name, description and
    /// server metadata. In <see cref="WrapperToolMode.Lazy"/> mode the returned matches are
    /// activated into the tool list before this returns.
    /// </summary>
    public async Task<FindToolsResult> FindAsync(string query, int limit, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);

        var normalizedQuery = Normalize(query);
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
            return new FindToolsResult([], []);

        var matches = new List<FindToolsMatch>();
        var skipped = new List<string>();

        foreach (var server in _registry.GetAll().Where(s => s.Enabled).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<DownstreamToolWrapper> wrappers;
            List<ToolDetail> details;
            try
            {
                wrappers = await GetWrappersAsync(server.Name, ct);
                details = await _toolIndex.GetToolsForServerAsync(server.Name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "find_tools skipped '{Server}': {Message}", server.Name, ex.Message);
                skipped.Add($"{server.Name}: {ex.Message}");
                continue;
            }

            var detailByName = details.ToDictionary(d => d.Name, StringComparer.Ordinal);

            foreach (var wrapper in wrappers)
            {
                if (!detailByName.TryGetValue(wrapper.ToolName, out var detail))
                    continue;

                var score = Score(normalizedQuery, tokens, wrapper, server, detail);
                if (score > 0)
                    matches.Add(new FindToolsMatch(wrapper, server, detail, score));
            }
        }

        var top = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Wrapper.ProtocolTool.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();

        if (Mode == WrapperToolMode.Lazy && top.Count > 0)
            await ActivateAsync(top.Select(m => m.Wrapper), ct);

        return new FindToolsResult(top, skipped);
    }

    /// <summary>Marks the wrappers as activated (Lazy mode) and resyncs the tool collection.</summary>
    public async Task ActivateAsync(IEnumerable<DownstreamToolWrapper> wrappers, CancellationToken ct = default)
    {
        foreach (var wrapper in wrappers)
            _activated[wrapper.ProtocolTool.Name] = wrapper.ServerName;

        await SyncAsync(ct);
    }

    /// <summary>Activates every wrapper of one server (Lazy mode) and resyncs the tool collection.</summary>
    public async Task ActivateServerAsync(string serverName, CancellationToken ct = default)
    {
        var wrappers = await GetWrappersAsync(serverName, ct);
        await ActivateAsync(wrappers, ct);
    }

    /// <summary>True when a wrapper with this name is currently in the tool list.</summary>
    public bool IsActive(string wrapperName)
        => ToolCollection.TryGetPrimitive(wrapperName, out var tool) && tool is DownstreamToolWrapper;

    /// <summary>
    /// Explains a <c>tools/call</c> for a name the aggregator does not currently expose, so the
    /// caller can self-correct instead of concluding the tool is broken. Distinguishes a server
    /// that was renamed or removed, a disabled server, an unknown tool on a known server, and a
    /// real wrapper that simply was not in the caller's tool list yet — in which case the wrapper
    /// is activated so a retry (after a tool-list refresh) succeeds.
    /// </summary>
    public async Task<string> BuildUnknownToolHintAsync(string toolName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);

        if (!WrapperNaming.TryParse(toolName, out var serverName, out var downstreamTool))
        {
            var aggregatorTools = ToolCollection
                .Where(t => t is not DownstreamToolWrapper)
                .Select(t => t.ProtocolTool.Name)
                .Order(StringComparer.Ordinal);
            return $"Unknown tool '{toolName}'. Aggregator tools: [{string.Join(", ", aggregatorTools)}]. " +
                   $"Downstream tools are typed tools named '{{server}}{WrapperNaming.Separator}{{tool}}'. " +
                   $"Call find_tools(query: \"{toolName}\") to get current tool names and schemas, " +
                   "or list_services to browse servers.";
        }

        if (!_registry.TryGet(serverName, out var server) || server is null)
        {
            var registered = _registry.GetAll()
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .Order(StringComparer.OrdinalIgnoreCase);
            return $"Unknown tool '{toolName}': no server named '{serverName}' is registered. " +
                   "It may have been renamed or removed since your tool list was loaded. " +
                   $"Registered servers: [{string.Join(", ", registered)}]. " +
                   $"Call find_tools(query: \"{downstreamTool}\") to find the tool's current name, then refresh your tool list.";
        }

        if (!server.Enabled)
        {
            return $"Tool '{toolName}' is not available: server '{server.Name}' is disabled. " +
                   $"Call enable_service(serverName: \"{server.Name}\") to re-enable it, then refresh your tool list and retry.";
        }

        IReadOnlyList<DownstreamToolWrapper> wrappers;
        try
        {
            wrappers = await GetWrappersAsync(server.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Tool '{toolName}' is not available: server '{server.Name}' is registered but could not be reached " +
                   $"({ex.Message}). Retry later, or call refresh_service(serverName: \"{server.Name}\").";
        }

        var wrapper = wrappers.FirstOrDefault(w => string.Equals(w.ProtocolTool.Name, toolName, StringComparison.Ordinal));
        if (wrapper is null)
        {
            var names = wrappers.Select(w => w.ProtocolTool.Name).Order(StringComparer.Ordinal);
            return $"Unknown tool '{toolName}': server '{server.Name}' has no tool '{downstreamTool}'. " +
                   $"Its tools: [{string.Join(", ", names)}]. " +
                   $"Call get_service_details(serverName: \"{server.Name}\") for their input schemas, then refresh your tool list.";
        }

        // The wrapper exists; the caller's tool list is simply behind. Make it callable now.
        var activatedNow = !IsActive(wrapper.ProtocolTool.Name);
        if (activatedNow)
            await ActivateAsync([wrapper], ct);

        var schema = wrapper.ProtocolTool.InputSchema.GetRawText();
        var state = activatedNow
            ? "It was not in the aggregator's tool list yet; it has been activated and tools/list_changed was sent."
            : "It is in the aggregator's tool list now.";
        return $"Tool '{toolName}' exists on server '{server.Name}' but was not in your tool list. {state} " +
               "Refresh your tool list and retry, or call it now via " +
               $"invoke_tool(serverName: \"{server.Name}\", toolName: \"{wrapper.ToolName}\", arguments: <JSON object as a string>). " +
               $"Input schema: {schema}";
    }

    /// <summary>
    /// Reconciles the tool collection with the desired wrapper set: every wrapper of every enabled
    /// server in <see cref="WrapperToolMode.Eager"/> mode, or the activated wrappers that still
    /// exist in <see cref="WrapperToolMode.Lazy"/> mode. Servers whose tools cannot be listed are
    /// skipped and their previous wrappers removed. All adds and removes happen under one deferral
    /// so the SDK sees a single <c>Changed</c> event per sync.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            await _registry.EnsureLoadedAsync(ct);
            var enabled = _registry.GetAll().Where(s => s.Enabled).ToList();
            var enabledNames = enabled.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            PruneState(enabledNames);

            var serversToBuild = Mode == WrapperToolMode.Eager
                ? enabled
                : enabled.Where(s => _activated.Values.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            var desired = new Dictionary<string, DownstreamToolWrapper>(StringComparer.Ordinal);

            foreach (var server in serversToBuild)
            {
                IReadOnlyList<DownstreamToolWrapper> wrappers;
                try
                {
                    wrappers = await GetWrappersAsync(server.Name, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Skipping wrapper tools for '{Server}' (unavailable): {Message}", server.Name, ex.Message);
                    _built.TryRemove(server.Name, out _);
                    continue;
                }

                foreach (var wrapper in wrappers)
                {
                    if (Mode == WrapperToolMode.Lazy && !_activated.ContainsKey(wrapper.ProtocolTool.Name))
                        continue;

                    if (!desired.TryAdd(wrapper.ProtocolTool.Name, wrapper))
                    {
                        _logger.LogWarning(
                            "Wrapper name '{Wrapper}' is claimed by more than one server; keeping the first",
                            wrapper.ProtocolTool.Name);
                    }
                }
            }

            var collection = ToolCollection;
            var added = 0;
            var removed = 0;

            using (collection.DeferChangedEvents())
            {
                foreach (var current in collection.OfType<DownstreamToolWrapper>().ToList())
                {
                    if (!desired.TryGetValue(current.ProtocolTool.Name, out var wanted) || !ReferenceEquals(wanted, current))
                    {
                        if (collection.Remove(current))
                            removed++;
                    }
                }

                foreach (var wrapper in desired.Values)
                {
                    if (collection.TryGetPrimitive(wrapper.ProtocolTool.Name, out var existing))
                    {
                        if (ReferenceEquals(existing, wrapper))
                            continue;

                        // A non-wrapper tool already owns this name. Never displace the
                        // aggregator's own tools.
                        _logger.LogWarning(
                            "Wrapper '{Wrapper}' collides with an aggregator tool of the same name and was not added",
                            wrapper.ProtocolTool.Name);
                        continue;
                    }

                    if (collection.TryAdd(wrapper))
                        added++;
                }
            }

            if (added > 0 || removed > 0)
            {
                _logger.LogInformation(
                    "Wrapper tools synced ({Mode}): +{Added} -{Removed}, {Active} active",
                    Mode, added, removed, desired.Count);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void PruneState(HashSet<string> enabledNames)
    {
        foreach (var key in _built.Keys.Where(k => !enabledNames.Contains(k)).ToList())
            _built.TryRemove(key, out _);

        foreach (var kvp in _activated.Where(kvp => !enabledNames.Contains(kvp.Value)).ToList())
            _activated.TryRemove(kvp.Key, out _);
    }

    private void OnRegistryChanged()
    {
        try
        {
            var enabledNames = _registry.GetAll().Where(s => s.Enabled).Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PruneState(enabledNames);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to prune wrapper state on registry change");
        }

        ScheduleSync("registry changed");
    }

    private void OnToolsChanged(string serverName)
    {
        // The index has already dropped or replaced its entry; the next GetWrappersAsync re-reads
        // it and reuses any wrapper whose name and schema are unchanged. Keeping _built here is
        // what makes a refresh_service after an unchanged downstream a no-op for the client.
        ScheduleSync($"tools changed for '{serverName}'");
    }

    /// <summary>
    /// Fire-and-forget resync. Requests that arrive before the queued sync starts are coalesced;
    /// one that arrives while a sync is running queues exactly one more.
    /// </summary>
    private void ScheduleSync(string reason)
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 1)
            return;

        _scheduledSync = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _syncQueued, 0);
            try
            {
                await SyncAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background wrapper sync failed ({Reason})", reason);
            }
        });
    }

    // ---------------------------------------------------------------- search scoring

    private static string Normalize(string value)
        => new(value.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            if (current.Length > 1)
                tokens.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 1)
            tokens.Add(current.ToString());

        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    private static int Score(string normalizedQuery, List<string> tokens, DownstreamToolWrapper wrapper, RegisteredServer server, ToolDetail detail)
    {
        var wrapperName = wrapper.ProtocolTool.Name.ToLowerInvariant();
        var toolName = wrapper.ToolName.ToLowerInvariant();
        var toolNameTokens = Tokenize(wrapper.ToolName);
        var description = (detail.Description ?? string.Empty).ToLowerInvariant();
        var serverText = string.Join(' ', server.Name, server.DisplayName ?? string.Empty,
            server.AiSummary ?? server.Description ?? string.Empty).ToLowerInvariant();

        var score = 0;

        if (normalizedQuery.Length > 0
            && (string.Equals(normalizedQuery, wrapperName.Replace('-', '_'), StringComparison.Ordinal)
                || string.Equals(normalizedQuery, toolName.Replace('-', '_'), StringComparison.Ordinal)))
        {
            score += 1000;
        }

        foreach (var token in tokens)
        {
            if (toolNameTokens.Contains(token, StringComparer.Ordinal))
                score += 80;
            else if (toolName.Contains(token, StringComparison.Ordinal))
                score += 50;
            else if (wrapperName.Contains(token, StringComparison.Ordinal))
                score += 30;

            if (description.Contains(token, StringComparison.Ordinal))
                score += 10;

            if (serverText.Contains(token, StringComparison.Ordinal))
                score += 5;
        }

        return score;
    }
}
