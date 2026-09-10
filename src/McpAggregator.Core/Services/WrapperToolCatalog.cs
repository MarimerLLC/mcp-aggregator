using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Services;

/// <summary>One <c>find_tools</c> tool hit.</summary>
public sealed record FindToolsMatch(DownstreamToolWrapper Wrapper, RegisteredServer Server, ToolDetail Detail, int Score);

/// <summary>One <c>find_tools</c> prompt hit.</summary>
public sealed record FindPromptsMatch(DownstreamPromptWrapper Wrapper, RegisteredServer Server, PromptDetail Detail, int Score);

/// <summary>One <c>find_tools</c> resource hit.</summary>
public sealed record FindResourcesMatch(DownstreamResourceWrapper Wrapper, RegisteredServer Server, ResourceDetail Detail, int Score);

/// <summary>Result of <see cref="WrapperToolCatalog.FindAsync"/>.</summary>
/// <param name="Matches">Best tool matches first.</param>
/// <param name="SkippedServers">Enabled servers whose tools could not be listed, with the reason.</param>
/// <param name="Prompts">Best prompt matches first.</param>
/// <param name="Resources">Best resource and resource-template matches first.</param>
public sealed record FindToolsResult(
    IReadOnlyList<FindToolsMatch> Matches,
    IReadOnlyList<string> SkippedServers,
    IReadOnlyList<FindPromptsMatch> Prompts,
    IReadOnlyList<FindResourcesMatch> Resources);

/// <summary>
/// Owns the typed wrapper tools (<c>{server}__{tool}</c>) and the proxied prompts
/// (<c>{server}__{prompt}</c>) the aggregator exposes for downstream tools and prompts.
/// <para>
/// In <see cref="WrapperToolMode.Eager"/> mode every wrapper of every enabled server is kept in
/// the process-wide <see cref="McpServerOptions.ToolCollection"/> /
/// <see cref="McpServerOptions.PromptCollection"/>; the SDK server watches those collections and
/// sends <c>notifications/tools/list_changed</c> / <c>notifications/prompts/list_changed</c> itself.
/// </para>
/// <para>
/// In <see cref="WrapperToolMode.Lazy"/> mode the shared collections are never touched. Activation
/// is <b>per session</b>: <c>find_tools</c> and <c>get_service_details</c> record the wrappers for
/// the calling <see cref="McpServer"/> only, the aggregator's <c>tools/list</c> and
/// <c>prompts/list</c> handlers append that session's wrappers, and the <c>tools/call</c> /
/// <c>prompts/get</c> fallbacks dispatch any wrapper by name whether or not it is listed. One
/// client's discovery therefore never enlarges another client's list (which is the whole point of
/// Lazy: not filling context windows with tools nobody asked for). A session with no memory
/// between requests — stateless HTTP — simply never lists wrappers and relies on calling them by
/// name.
/// </para>
/// <para>
/// Invariant: the catalog only ever adds or removes <see cref="DownstreamToolWrapper"/> /
/// <see cref="DownstreamPromptWrapper"/> / <see cref="DownstreamResourceWrapper"/> instances in
/// the shared collections, and only in Eager mode. The aggregator's own attributed tools are never
/// touched. Resources (issue #45) follow the prompt pipeline exactly, keyed by their
/// <c>mcp-aggregator://{server}/{uri}</c> URI instead of a <c>{server}__{name}</c> name.
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

    // Last built wrapper set per server, keyed by server name. Source of instance reuse only;
    // the index is the source of truth.
    private readonly ConcurrentDictionary<string, IReadOnlyList<DownstreamToolWrapper>> _built = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<DownstreamPromptWrapper>> _builtPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<DownstreamResourceWrapper>> _builtResources = new(StringComparer.OrdinalIgnoreCase);

    // Lazy mode: wrappers activated per session. The SDK hands tools a fresh McpServer facade per
    // request, so the facade cannot be the key. What is stable: the session id where the transport
    // has one (stateful HTTP), otherwise the McpServerOptions instance — one per stdio process, and
    // one per request on stateless HTTP, which is exactly "no memory between requests". Id-keyed
    // state is swept after ConnectionIdleTimeout; options-keyed state is weakly held.
    private readonly ConcurrentDictionary<string, SessionActivation> _sessionsById = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<McpServerOptions, SessionActivation> _sessionsByOptions = new();

    private readonly ConcurrentDictionary<string, byte> _warnedLongNames = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private int _syncQueued;
    private volatile Task _scheduledSync = Task.CompletedTask;

    // The aggregator's own administrative tools. In Lazy mode they are never in the shared
    // collection; they are disclosed per session by show_admin_tools or by being called by name.
    private readonly AdminToolSet _adminTools;

    private sealed class SessionActivation
    {
        public ConcurrentDictionary<string, McpServerTool> Tools { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, McpServerPrompt> Prompts { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, McpServerResource> Resources { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }

    private SessionActivation? TryGetSession(McpServer? session)
    {
        if (session is null)
            return null;

        if (session.SessionId is { Length: > 0 } id)
            return _sessionsById.TryGetValue(id, out var byId) ? Touch(byId) : null;

        return session.ServerOptions is { } options && _sessionsByOptions.TryGetValue(options, out var byOptions)
            ? Touch(byOptions)
            : null;
    }

    private SessionActivation? GetOrCreateSession(McpServer? session)
    {
        if (session is null)
            return null;

        if (session.SessionId is { Length: > 0 } id)
        {
            SweepIdleSessions();
            return Touch(_sessionsById.GetOrAdd(id, _ => new SessionActivation()));
        }

        return session.ServerOptions is { } options
            ? Touch(_sessionsByOptions.GetOrCreateValue(options))
            : null;
    }

    private static SessionActivation Touch(SessionActivation state)
    {
        state.LastTouched = DateTimeOffset.UtcNow;
        return state;
    }

    private void SweepIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.ConnectionIdleTimeout;
        foreach (var kvp in _sessionsById)
        {
            if (kvp.Value.LastTouched < cutoff)
                _sessionsById.TryRemove(kvp.Key, out _);
        }
    }

    private IEnumerable<SessionActivation> AllSessions()
        => _sessionsById.Values.Concat(_sessionsByOptions.Select(kvp => kvp.Value));

    public WrapperToolCatalog(
        ServerRegistry registry,
        ToolIndex toolIndex,
        ToolProxyHandler proxy,
        IOptions<AggregatorOptions> options,
        IOptions<McpServerOptions> mcpServerOptions,
        AdminToolSet adminTools,
        ILogger<WrapperToolCatalog> logger)
    {
        _registry = registry;
        _toolIndex = toolIndex;
        _proxy = proxy;
        _options = options.Value;
        _mcpServerOptions = mcpServerOptions;
        _adminTools = adminTools;
        _logger = logger;

        _registry.RegistryChanged += OnRegistryChanged;
        _toolIndex.ToolsChanged += OnToolsChanged;
        _toolIndex.PromptsChanged += OnPromptsChanged;
        _toolIndex.ResourcesChanged += OnResourcesChanged;
    }

    public WrapperToolMode Mode => _options.WrapperMode;

    /// <summary>The wrapper tools currently in the shared, process-wide tool list (Eager mode).</summary>
    public IReadOnlyList<DownstreamToolWrapper> ActiveWrappers
        => ToolCollection.OfType<DownstreamToolWrapper>().ToList();

    /// <summary>The proxied prompts currently in the shared, process-wide prompt list (Eager mode).</summary>
    public IReadOnlyList<DownstreamPromptWrapper> ActivePromptWrappers
        => PromptCollection.OfType<DownstreamPromptWrapper>().ToList();

    /// <summary>The bridged resources currently in the shared, process-wide resource list (Eager mode).</summary>
    public IReadOnlyList<DownstreamResourceWrapper> ActiveResourceWrappers
        => ResourceCollection.OfType<DownstreamResourceWrapper>().ToList();

    /// <summary>The administrative tools (listed in Eager mode; disclosed per session in Lazy mode).</summary>
    public IReadOnlyList<McpServerTool> AdminTools => _adminTools.Tools;

    /// <summary>
    /// Lazy mode: an administrative tool by name, for by-name dispatch of a tool the session has
    /// not been shown. False in Eager mode, where they are in the shared collection anyway.
    /// </summary>
    public bool TryGetHiddenTool(string name, out McpServerTool tool)
    {
        if (Mode == WrapperToolMode.Lazy && _adminTools.ByName.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

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
            // AddAggregatorMcpServer pre-assigns this; the fallback only matters for a catalog
            // constructed outside that wiring.
            return options.ToolCollection ??= [];
        }
    }

    private McpServerPrimitiveCollection<McpServerPrompt> PromptCollection
    {
        get
        {
            var options = _mcpServerOptions.Value;
            return options.PromptCollection ??= [];
        }
    }

    private McpServerResourceCollection ResourceCollection
    {
        get
        {
            var options = _mcpServerOptions.Value;
            return options.ResourceCollection ??= new McpServerResourceCollection();
        }
    }

    // ---------------------------------------------------------------- building

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
    /// Resolves a wrapper by its <c>{server}__{tool}</c> name against the live index, whether or
    /// not it is listed anywhere. Null when the name does not parse, the server is unknown or
    /// disabled, or the server has no such tool. Throws when the server is registered and enabled
    /// but cannot be reached.
    /// </summary>
    public async Task<DownstreamToolWrapper?> ResolveAsync(string wrapperName, CancellationToken ct = default)
    {
        if (!WrapperNaming.TryParse(wrapperName, out var serverName, out _))
            return null;

        await _registry.EnsureLoadedAsync(ct);
        if (!_registry.TryGet(serverName, out var server) || server is null || !server.Enabled)
            return null;

        var wrappers = await GetWrappersAsync(server.Name, ct);
        return wrappers.FirstOrDefault(w => string.Equals(w.ProtocolTool.Name, wrapperName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds (or reuses) the proxied prompt set for one server from the index. A wrapper instance
    /// is reused when its name and argument fingerprint are unchanged so the prompt collection is
    /// not churned by a refresh that changed nothing. A server without prompt support yields an
    /// empty list.
    /// </summary>
    public async Task<IReadOnlyList<DownstreamPromptWrapper>> GetPromptWrappersAsync(string serverName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var prompts = await _toolIndex.GetPromptsForServerAsync(server.Name, ct);

        var previous = _builtPrompts.TryGetValue(server.Name, out var existing)
            ? existing.ToDictionary(w => w.ProtocolPrompt.Name, StringComparer.Ordinal)
            : new Dictionary<string, DownstreamPromptWrapper>(StringComparer.Ordinal);

        var wrappers = new List<DownstreamPromptWrapper>(prompts.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prompt in prompts)
        {
            if (prompt.Protocol is null)
                continue;

            var name = WrapperNaming.For(server.Name, prompt.Name);
            if (!seen.Add(name))
            {
                _logger.LogWarning(
                    "Two prompts on '{Server}' map to the same name '{Wrapper}' after sanitization; keeping the first",
                    server.Name, name);
                continue;
            }

            var fingerprint = DownstreamPromptWrapper.Fingerprint(prompt.Protocol);
            if (previous.TryGetValue(name, out var reusable)
                && string.Equals(reusable.ArgumentsFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(reusable.ServerId, server.Id, StringComparison.Ordinal))
            {
                wrappers.Add(reusable);
                continue;
            }

            if (name.Length > WrapperNaming.HostNameLengthLimit && _warnedLongNames.TryAdd(name, 0))
            {
                _logger.LogWarning(
                    "Prompt name '{Wrapper}' is {Length} characters; hosts that cap prompt names at {Limit} may drop it",
                    name, name.Length, WrapperNaming.HostNameLengthLimit);
            }

            wrappers.Add(new DownstreamPromptWrapper(server, prompt.Protocol, _proxy, _logger));
        }

        _builtPrompts[server.Name] = wrappers;
        return wrappers;
    }

    /// <summary>
    /// Resolves a proxied prompt by its <c>{server}__{prompt}</c> name against the live index,
    /// whether or not it is listed anywhere. Null when the name does not parse, the server is
    /// unknown or disabled, or the server has no such prompt. Throws when the server is registered
    /// and enabled but cannot be reached.
    /// </summary>
    public async Task<DownstreamPromptWrapper?> ResolvePromptAsync(string wrapperName, CancellationToken ct = default)
    {
        if (!WrapperNaming.TryParse(wrapperName, out var serverName, out _))
            return null;

        await _registry.EnsureLoadedAsync(ct);
        if (!_registry.TryGet(serverName, out var server) || server is null || !server.Enabled)
            return null;

        var wrappers = await GetPromptWrappersAsync(server.Name, ct);
        return wrappers.FirstOrDefault(w => string.Equals(w.ProtocolPrompt.Name, wrapperName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds (or reuses) the bridged resource set for one server from the index: one wrapper per
    /// downstream resource and per resource template. A wrapper instance is reused when its URI
    /// and fingerprint are unchanged so the resource collection is not churned by a refresh that
    /// changed nothing. A server without resource support yields an empty list. Two downstream
    /// URIs that differ only by fragment collide in the SDK collection (its comparer ignores
    /// fragments); the first one wins and the second is logged.
    /// </summary>
    public async Task<IReadOnlyList<DownstreamResourceWrapper>> GetResourceWrappersAsync(string serverName, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(serverName);
        var resources = await _toolIndex.GetResourcesForServerAsync(server.Name, ct);

        var previous = _builtResources.TryGetValue(server.Name, out var existing)
            ? existing.ToDictionary(w => w.Uri, StringComparer.Ordinal)
            : new Dictionary<string, DownstreamResourceWrapper>(StringComparer.Ordinal);

        var wrappers = new List<DownstreamResourceWrapper>(resources.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new McpServerResourceCollection();

        foreach (var resource in resources)
        {
            if (resource.Protocol is null && resource.ProtocolTemplate is null)
                continue;

            var uri = resource.Uri;
            if (!seen.Add(uri))
            {
                _logger.LogWarning(
                    "Two resources on '{Server}' map to the same URI '{Uri}'; keeping the first",
                    server.Name, uri);
                continue;
            }

            var fingerprint = resource.Protocol is { } plain
                ? DownstreamResourceWrapper.ComputeFingerprint(plain)
                : DownstreamResourceWrapper.ComputeFingerprint(resource.ProtocolTemplate!);

            DownstreamResourceWrapper wrapper;
            if (previous.TryGetValue(uri, out var reusable)
                && string.Equals(reusable.Fingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(reusable.ServerId, server.Id, StringComparison.Ordinal))
            {
                wrapper = reusable;
            }
            else
            {
                wrapper = resource.Protocol is { } r
                    ? new DownstreamResourceWrapper(server, r, _proxy, _logger)
                    : new DownstreamResourceWrapper(server, resource.ProtocolTemplate!, _proxy, _logger);
            }

            // The SDK collection compares plain URIs as System.Uri (fragment ignored, host
            // case-insensitive); a second wrapper it considers equal could never be added.
            if (!seenKeys.TryAdd(wrapper))
            {
                _logger.LogWarning(
                    "Resource '{Uri}' on '{Server}' is indistinguishable from an earlier one to the resource collection (fragment or case only); keeping the first",
                    uri, server.Name);
                continue;
            }

            wrappers.Add(wrapper);
        }

        _builtResources[server.Name] = wrappers;
        return wrappers;
    }

    /// <summary>
    /// Resolves a bridged resource by its aggregator URI against the live index, whether or not it
    /// is listed anywhere: an exact match on a plain resource first, then the first template whose
    /// pattern the URI expands. Null when the URI does not parse, the server is unknown or disabled,
    /// or nothing on the server matches. Throws when the server is registered and enabled but
    /// cannot be reached.
    /// </summary>
    public async Task<DownstreamResourceWrapper?> ResolveResourceAsync(string aggregatorUri, CancellationToken ct = default)
    {
        if (!ResourceUriNaming.TryParse(aggregatorUri, out var serverName, out _))
            return null;

        await _registry.EnsureLoadedAsync(ct);
        if (!_registry.TryGet(serverName, out var server) || server is null || !server.Enabled)
            return null;

        var wrappers = await GetResourceWrappersAsync(server.Name, ct);
        return wrappers.FirstOrDefault(w => !w.IsTemplate && w.IsMatch(aggregatorUri))
            ?? wrappers.FirstOrDefault(w => w.IsTemplate && w.IsMatch(aggregatorUri));
    }

    // ---------------------------------------------------------------- search

    /// <summary>
    /// Searches every enabled server's tools, prompts and resources for <paramref name="query"/>.
    /// An exact name match ranks first, then token hits on the downstream name, wrapper name (or
    /// aggregator URI), description and server metadata. <paramref name="limit"/> applies to each
    /// kind separately. In <see cref="WrapperToolMode.Lazy"/> mode the returned matches are
    /// activated for <paramref name="session"/> before this returns.
    /// </summary>
    public async Task<FindToolsResult> FindAsync(string query, int limit, McpServer? session, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);

        var normalizedQuery = Normalize(query);
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
            return new FindToolsResult([], [], [], []);

        var matches = new List<FindToolsMatch>();
        var promptMatches = new List<FindPromptsMatch>();
        var resourceMatches = new List<FindResourcesMatch>();
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

                var score = Score(normalizedQuery, tokens, wrapper.ProtocolTool.Name, wrapper.ToolName, detail.Description, server);
                if (score > 0)
                    matches.Add(new FindToolsMatch(wrapper, server, detail, score));
            }

            // Prompts are best-effort: the server is reachable (its tools listed), so a prompt
            // failure here is a fact about its prompt support, not a reason to skip the server.
            IReadOnlyList<DownstreamPromptWrapper> promptWrappers;
            List<PromptDetail> promptDetails;
            try
            {
                promptWrappers = await GetPromptWrappersAsync(server.Name, ct);
                promptDetails = await _toolIndex.GetPromptsForServerAsync(server.Name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "find_tools could not list prompts on '{Server}': {Message}", server.Name, ex.Message);
                promptWrappers = [];
                promptDetails = [];
            }

            var promptByName = promptDetails.ToDictionary(d => d.Name, StringComparer.Ordinal);

            foreach (var wrapper in promptWrappers)
            {
                if (!promptByName.TryGetValue(wrapper.PromptName, out var detail))
                    continue;

                var score = Score(normalizedQuery, tokens, wrapper.ProtocolPrompt.Name, wrapper.PromptName, detail.Description, server);
                if (score > 0)
                    promptMatches.Add(new FindPromptsMatch(wrapper, server, detail, score));
            }

            // Resources likewise (issue #45). The aggregator URI stands in for the wrapper name so
            // URI tokens score; title and MIME type ride along with the description.
            IReadOnlyList<DownstreamResourceWrapper> resourceWrappers;
            List<ResourceDetail> resourceDetails;
            try
            {
                resourceWrappers = await GetResourceWrappersAsync(server.Name, ct);
                resourceDetails = await _toolIndex.GetResourcesForServerAsync(server.Name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "find_tools could not list resources on '{Server}': {Message}", server.Name, ex.Message);
                continue;
            }

            var resourceByUri = resourceDetails.ToDictionary(d => d.Uri, StringComparer.Ordinal);

            foreach (var wrapper in resourceWrappers)
            {
                if (!resourceByUri.TryGetValue(wrapper.Uri, out var detail))
                    continue;

                var text = string.Join(' ', detail.Title ?? string.Empty, detail.Description ?? string.Empty, detail.MimeType ?? string.Empty);
                var score = Score(normalizedQuery, tokens, wrapper.Uri, detail.Name, text, server);
                if (score > 0)
                    resourceMatches.Add(new FindResourcesMatch(wrapper, server, detail, score));
            }
        }

        var top = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Wrapper.ProtocolTool.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();

        var topPrompts = promptMatches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Wrapper.ProtocolPrompt.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();

        var topResources = resourceMatches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Wrapper.Uri, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();

        if (top.Count > 0)
            await ActivateAsync(session, top.Select(m => m.Wrapper), ct);

        if (topPrompts.Count > 0)
            await ActivatePromptsAsync(session, topPrompts.Select(m => m.Wrapper), ct);

        if (topResources.Count > 0)
            await ActivateResourcesAsync(session, topResources.Select(m => m.Wrapper), ct);

        return new FindToolsResult(top, skipped, topPrompts, topResources);
    }

    // ---------------------------------------------------------------- per-session activation (Lazy)

    /// <summary>
    /// Lazy mode: makes the wrappers part of <paramref name="session"/>'s tool list and tells that
    /// session the list changed. No-op in Eager mode (everything is already listed) and when there
    /// is no session to remember it for.
    /// </summary>
    public async Task ActivateAsync(McpServer? session, IEnumerable<McpServerTool> tools, CancellationToken ct = default)
    {
        if (Mode != WrapperToolMode.Lazy)
            return;

        var state = GetOrCreateSession(session);
        if (state is null)
            return;

        var added = 0;
        foreach (var tool in tools)
        {
            if (state.Tools.TryAdd(tool.ProtocolTool.Name, tool))
                added++;
            else
                state.Tools[tool.ProtocolTool.Name] = tool; // refreshed instance
        }

        if (added == 0)
            return;

        _logger.LogInformation("Activated {Count} tool(s) for a session ({Total} active in it)", added, state.Tools.Count);

        try
        {
            // The shared collection did not change, so the SDK will not announce anything; tell
            // this one session ourselves. Delivered as a broadcast, which reaches pre-2026-07-28
            // clients (every real host today); a 2026-07-28 client would need a subscriptions/listen
            // stream and the SDK offers no public way to fan out into one from here.
            await session!.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not send tools/list_changed to the session");
        }
    }

    /// <summary>
    /// Lazy mode: makes the prompts part of <paramref name="session"/>'s prompt list and tells that
    /// session the list changed. No-op in Eager mode and when there is no session to remember it for.
    /// </summary>
    public async Task ActivatePromptsAsync(McpServer? session, IEnumerable<McpServerPrompt> prompts, CancellationToken ct = default)
    {
        if (Mode != WrapperToolMode.Lazy)
            return;

        var state = GetOrCreateSession(session);
        if (state is null)
            return;

        var added = 0;
        foreach (var prompt in prompts)
        {
            if (state.Prompts.TryAdd(prompt.ProtocolPrompt.Name, prompt))
                added++;
            else
                state.Prompts[prompt.ProtocolPrompt.Name] = prompt; // refreshed instance
        }

        if (added == 0)
            return;

        _logger.LogInformation("Activated {Count} prompt(s) for a session ({Total} active in it)", added, state.Prompts.Count);

        try
        {
            await session!.SendNotificationAsync(NotificationMethods.PromptListChangedNotification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not send prompts/list_changed to the session");
        }
    }

    /// <summary>
    /// Lazy mode: makes the resources part of <paramref name="session"/>'s resource list and tells
    /// that session the list changed. No-op in Eager mode and when there is no session to remember
    /// it for. Keyed by aggregator URI (or URI template).
    /// </summary>
    public async Task ActivateResourcesAsync(McpServer? session, IEnumerable<McpServerResource> resources, CancellationToken ct = default)
    {
        if (Mode != WrapperToolMode.Lazy)
            return;

        var state = GetOrCreateSession(session);
        if (state is null)
            return;

        var added = 0;
        foreach (var resource in resources)
        {
            var key = resource.ProtocolResourceTemplate.UriTemplate;
            if (state.Resources.TryAdd(key, resource))
                added++;
            else
                state.Resources[key] = resource; // refreshed instance
        }

        if (added == 0)
            return;

        _logger.LogInformation("Activated {Count} resource(s) for a session ({Total} active in it)", added, state.Resources.Count);

        try
        {
            await session!.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not send resources/list_changed to the session");
        }
    }

    /// <summary>
    /// Lazy mode: activates every wrapper tool, proxied prompt and bridged resource of one server
    /// for <paramref name="session"/>. Prompts and resources are best-effort: a server whose tools
    /// listed but whose prompts or resources cannot be fetched still gets the rest activated.
    /// </summary>
    public async Task ActivateServerAsync(McpServer? session, string serverName, CancellationToken ct = default)
    {
        var wrappers = await GetWrappersAsync(serverName, ct);
        await ActivateAsync(session, wrappers, ct);

        try
        {
            var prompts = await GetPromptWrappersAsync(serverName, ct);
            await ActivatePromptsAsync(session, prompts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list prompts on '{Server}' for activation: {Message}", serverName, ex.Message);
        }

        try
        {
            var resources = await GetResourceWrappersAsync(serverName, ct);
            await ActivateResourcesAsync(session, resources, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list resources on '{Server}' for activation: {Message}", serverName, ex.Message);
        }
    }

    /// <summary>Lazy mode: discloses the administrative tools to <paramref name="session"/>.</summary>
    public Task ActivateAdminToolsAsync(McpServer? session, CancellationToken ct = default)
        => ActivateAsync(session, _adminTools.Tools, ct);

    /// <summary>The tools activated for <paramref name="session"/> (Lazy mode); empty otherwise.</summary>
    public IReadOnlyList<McpServerTool> ActivatedFor(McpServer? session)
        => TryGetSession(session) is { } state
            ? state.Tools.Values.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>True when this tool name is in <paramref name="session"/>'s tool list right now.</summary>
    public bool IsActive(string toolName, McpServer? session)
        => ToolCollection.TryGetPrimitive(toolName, out _)
           || (TryGetSession(session) is { } state && state.Tools.ContainsKey(toolName));

    /// <summary>The prompts activated for <paramref name="session"/> (Lazy mode); empty otherwise.</summary>
    public IReadOnlyList<McpServerPrompt> ActivatedPromptsFor(McpServer? session)
        => TryGetSession(session) is { } state
            ? state.Prompts.Values.OrderBy(p => p.ProtocolPrompt.Name, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>True when this prompt name is in <paramref name="session"/>'s prompt list right now.</summary>
    public bool IsPromptActive(string promptName, McpServer? session)
        => PromptCollection.TryGetPrimitive(promptName, out _)
           || (TryGetSession(session) is { } state && state.Prompts.ContainsKey(promptName));

    /// <summary>The resources activated for <paramref name="session"/> (Lazy mode); empty otherwise.</summary>
    public IReadOnlyList<McpServerResource> ActivatedResourcesFor(McpServer? session)
        => TryGetSession(session) is { } state
            ? state.Resources.Values.OrderBy(r => r.ProtocolResourceTemplate.UriTemplate, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>True when this aggregator URI (or template) is in <paramref name="session"/>'s resource list right now.</summary>
    public bool IsResourceActive(string uri, McpServer? session)
        => ResourceCollection.TryGetPrimitive(uri, out _)
           || (TryGetSession(session) is { } state && state.Resources.ContainsKey(uri));

    // ---------------------------------------------------------------- unknown-resource hint

    /// <summary>
    /// Explains a <c>resources/read</c> for a URI the aggregator could not dispatch. Distinguishes a
    /// URI that is not in aggregator form, a server that was renamed or removed, a disabled server,
    /// a URI nothing on a known server matches, and a server that exists but cannot be reached.
    /// </summary>
    public async Task<string> BuildUnknownResourceHintAsync(string? uri, CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);

        if (!ResourceUriNaming.TryParse(uri, out var serverName, out var downstreamUri))
        {
            return $"Unknown resource '{uri}'. Downstream resources are read as '{ResourceUriNaming.Prefix("{server}")}{{uri}}' " +
                   "(the downstream's own URI after the server prefix). Call find_tools or get_service_details to list them, " +
                   "or read_resource(serverName, uri) with the downstream URI.";
        }

        if (!_registry.TryGet(serverName, out var server) || server is null)
        {
            var registered = _registry.GetAll()
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .Order(StringComparer.OrdinalIgnoreCase);
            return $"Unknown resource '{uri}': no server named '{serverName}' is registered. " +
                   "It may have been renamed or removed since your resource list was loaded. " +
                   $"Registered servers: [{string.Join(", ", registered)}]. " +
                   "Call find_tools to find the resource's current URI, then refresh your resource list.";
        }

        if (!server.Enabled)
        {
            return $"Resource '{uri}' is not available: server '{server.Name}' is disabled. " +
                   $"Call enable_service(serverName: \"{server.Name}\") to re-enable it, then retry.";
        }

        IReadOnlyList<DownstreamResourceWrapper> wrappers;
        try
        {
            wrappers = await GetResourceWrappersAsync(server.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Resource '{uri}' is not available: server '{server.Name}' is registered but could not be reached " +
                   $"({ex.Message}). Retry later, or call refresh_service(serverName: \"{server.Name}\").";
        }

        var uris = wrappers.Select(w => w.Uri).Order(StringComparer.Ordinal);
        return $"Unknown resource '{uri}': server '{server.Name}' has no resource '{downstreamUri}' and no template it expands. " +
               $"Its resources and templates: [{string.Join(", ", uris)}]. " +
               $"Call get_service_details(serverName: \"{server.Name}\") for their descriptions.";
    }

    // ---------------------------------------------------------------- unknown-tool hint

    /// <summary>
    /// Explains a <c>tools/call</c> for a name the aggregator could not dispatch, so the caller can
    /// self-correct instead of concluding the tool is broken. Distinguishes a server that was
    /// renamed or removed, a disabled server, an unknown tool on a known server, and a server that
    /// exists but cannot be reached.
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
            var hiddenNote = Mode == WrapperToolMode.Lazy
                ? $" Administrative tools [{string.Join(", ", _adminTools.ByName.Keys.Order(StringComparer.Ordinal))}] are hidden until show_admin_tools, but callable by name."
                : string.Empty;
            return $"Unknown tool '{toolName}'. Aggregator tools: [{string.Join(", ", aggregatorTools)}].{hiddenNote} " +
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
                   $"Call enable_service(serverName: \"{server.Name}\") to re-enable it, then retry.";
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
                   $"Call get_service_details(serverName: \"{server.Name}\") for their input schemas.";
        }

        // Reachable only when the call could not be dispatched (no catalog-aware handler on this
        // server). The tool exists; say so and give the generic route.
        return $"Tool '{toolName}' exists on server '{server.Name}' but could not be dispatched by this endpoint. " +
               $"Call it via invoke_tool(serverName: \"{server.Name}\", toolName: \"{wrapper.ToolName}\", arguments: <JSON object as a string>). " +
               $"Input schema: {wrapper.ProtocolTool.InputSchema.GetRawText()}";
    }

    // ---------------------------------------------------------------- Eager sync of the shared collection

    /// <summary>
    /// Reconciles the shared tool collection with the desired wrapper set: every wrapper of every
    /// enabled server in <see cref="WrapperToolMode.Eager"/> mode, none in
    /// <see cref="WrapperToolMode.Lazy"/> mode. Servers whose tools cannot be listed are skipped
    /// and their previous wrappers removed. All adds and removes happen under one deferral so the
    /// SDK sees a single <c>Changed</c> event per sync. Also drops per-session activations for
    /// servers that are no longer enabled.
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

            var desired = new Dictionary<string, DownstreamToolWrapper>(StringComparer.Ordinal);
            var desiredPrompts = new Dictionary<string, DownstreamPromptWrapper>(StringComparer.Ordinal);
            var desiredResources = new Dictionary<string, DownstreamResourceWrapper>(StringComparer.Ordinal);

            if (Mode == WrapperToolMode.Eager)
            {
                foreach (var server in enabled)
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
                        _builtPrompts.TryRemove(server.Name, out _);
                        _builtResources.TryRemove(server.Name, out _);
                        continue;
                    }

                    foreach (var wrapper in wrappers)
                    {
                        if (!desired.TryAdd(wrapper.ProtocolTool.Name, wrapper))
                        {
                            _logger.LogWarning(
                                "Wrapper name '{Wrapper}' is claimed by more than one server; keeping the first",
                                wrapper.ProtocolTool.Name);
                        }
                    }

                    IReadOnlyList<DownstreamPromptWrapper> promptWrappers;
                    try
                    {
                        promptWrappers = await GetPromptWrappersAsync(server.Name, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Skipping prompts for '{Server}': {Message}", server.Name, ex.Message);
                        _builtPrompts.TryRemove(server.Name, out _);
                        promptWrappers = [];
                    }

                    foreach (var wrapper in promptWrappers)
                    {
                        if (!desiredPrompts.TryAdd(wrapper.ProtocolPrompt.Name, wrapper))
                        {
                            _logger.LogWarning(
                                "Prompt name '{Wrapper}' is claimed by more than one server; keeping the first",
                                wrapper.ProtocolPrompt.Name);
                        }
                    }

                    IReadOnlyList<DownstreamResourceWrapper> resourceWrappers;
                    try
                    {
                        resourceWrappers = await GetResourceWrappersAsync(server.Name, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Skipping resources for '{Server}': {Message}", server.Name, ex.Message);
                        _builtResources.TryRemove(server.Name, out _);
                        continue;
                    }

                    foreach (var wrapper in resourceWrappers)
                    {
                        // Server-prefixed URIs cannot collide across servers; this guards the map only.
                        desiredResources.TryAdd(wrapper.Uri, wrapper);
                    }
                }
            }

            var (added, removed) = Reconcile(ToolCollection, desired, w => w.ProtocolTool.Name, "tool");
            if (added > 0 || removed > 0)
            {
                _logger.LogInformation(
                    "Wrapper tools synced ({Mode}): +{Added} -{Removed}, {Active} active",
                    Mode, added, removed, desired.Count);
            }

            var (promptsAdded, promptsRemoved) = Reconcile(PromptCollection, desiredPrompts, w => w.ProtocolPrompt.Name, "prompt");
            if (promptsAdded > 0 || promptsRemoved > 0)
            {
                _logger.LogInformation(
                    "Proxied prompts synced ({Mode}): +{Added} -{Removed}, {Active} active",
                    Mode, promptsAdded, promptsRemoved, desiredPrompts.Count);
            }

            var (resourcesAdded, resourcesRemoved) = Reconcile(ResourceCollection, desiredResources, w => w.Uri, "resource");
            if (resourcesAdded > 0 || resourcesRemoved > 0)
            {
                _logger.LogInformation(
                    "Bridged resources synced ({Mode}): +{Added} -{Removed}, {Active} active",
                    Mode, resourcesAdded, resourcesRemoved, desiredResources.Count);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Brings the wrapper entries of one shared collection in line with <paramref name="desired"/>
    /// under a single <c>Changed</c> deferral. Only <typeparamref name="TWrapper"/> instances are
    /// ever removed; a non-wrapper primitive that owns a desired name is left alone.
    /// </summary>
    private (int Added, int Removed) Reconcile<TPrimitive, TWrapper>(
        McpServerPrimitiveCollection<TPrimitive> collection,
        Dictionary<string, TWrapper> desired,
        Func<TWrapper, string> nameOf,
        string kind)
        where TPrimitive : IMcpServerPrimitive
        where TWrapper : TPrimitive
    {
        var added = 0;
        var removed = 0;

        using (collection.DeferChangedEvents())
        {
            foreach (var current in collection.OfType<TWrapper>().ToList())
            {
                if (!desired.TryGetValue(nameOf(current), out var wanted) || !ReferenceEquals(wanted, current))
                {
                    if (collection.Remove(current))
                        removed++;
                }
            }

            foreach (var wrapper in desired.Values)
            {
                if (collection.TryGetPrimitive(nameOf(wrapper), out var existing))
                {
                    if (ReferenceEquals(existing, wrapper))
                        continue;

                    // A non-wrapper primitive already owns this name. Never displace the
                    // aggregator's own.
                    _logger.LogWarning(
                        "Wrapper '{Wrapper}' collides with an aggregator {Kind} of the same name and was not added",
                        nameOf(wrapper), kind);
                    continue;
                }

                if (collection.TryAdd(wrapper))
                    added++;
            }
        }

        return (added, removed);
    }

    private void PruneState(HashSet<string> enabledNames)
    {
        foreach (var key in _built.Keys.Where(k => !enabledNames.Contains(k)).ToList())
            _built.TryRemove(key, out _);

        foreach (var key in _builtPrompts.Keys.Where(k => !enabledNames.Contains(k)).ToList())
            _builtPrompts.TryRemove(key, out _);

        foreach (var key in _builtResources.Keys.Where(k => !enabledNames.Contains(k)).ToList())
            _builtResources.TryRemove(key, out _);

        foreach (var state in AllSessions())
        {
            foreach (var kvp in state.Tools.Where(kvp => kvp.Value is DownstreamToolWrapper w && !enabledNames.Contains(w.ServerName)).ToList())
                state.Tools.TryRemove(kvp.Key, out _);

            foreach (var kvp in state.Prompts.Where(kvp => kvp.Value is DownstreamPromptWrapper w && !enabledNames.Contains(w.ServerName)).ToList())
                state.Prompts.TryRemove(kvp.Key, out _);

            foreach (var kvp in state.Resources.Where(kvp => kvp.Value is DownstreamResourceWrapper w && !enabledNames.Contains(w.ServerName)).ToList())
                state.Resources.TryRemove(kvp.Key, out _);
        }
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
        // it and reuses any wrapper whose name and schema are unchanged. Session activations that
        // hold a stale instance are refreshed on the session's next find_tools/get_service_details
        // or call; the shared collection is reconciled here.
        ScheduleSync($"tools changed for '{serverName}'");
    }

    private void OnPromptsChanged(string serverName)
        => ScheduleSync($"prompts changed for '{serverName}'");

    private void OnResourcesChanged(string serverName)
        => ScheduleSync($"resources changed for '{serverName}'");

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

    /// <summary>
    /// Scores one tool or prompt: <paramref name="wrapperFullName"/> is its <c>{server}__{name}</c>
    /// name and <paramref name="downstreamName"/> the downstream's own name.
    /// </summary>
    private static int Score(string normalizedQuery, List<string> tokens, string wrapperFullName, string downstreamName, string? detailDescription, RegisteredServer server)
    {
        var wrapperName = wrapperFullName.ToLowerInvariant();
        var toolName = downstreamName.ToLowerInvariant();
        var toolNameTokens = Tokenize(downstreamName);
        var description = (detailDescription ?? string.Empty).ToLowerInvariant();
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
