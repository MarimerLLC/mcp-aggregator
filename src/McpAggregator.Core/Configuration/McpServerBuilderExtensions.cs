using System.Reflection;
using System.Text;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Configuration;

public static class McpServerBuilderExtensions
{
    public static IMcpServerBuilder AddAggregatorMcpServer(this IServiceCollection services)
    {
        var version = GetAggregatorVersion();

        // One tool collection for the life of the process, shared by every McpServerOptions
        // instance. The SDK's own options setup does `ToolCollection ??= []` and then adds the
        // attributed tools; on stateless HTTP it creates a *fresh* McpServerOptions per request via
        // IOptionsFactory, so without a pre-assigned collection every request would get its own
        // throwaway collection and never see the wrappers WrapperToolCatalog adds. This Configure
        // is registered before AddMcpServer() so it runs first; the PostConfigure below is the
        // belt-and-braces for any ordering the SDK might change.
        // The same holds for PromptCollection (issue #40): a non-null collection is also what makes
        // the SDK advertise the prompts capability with listChanged, so it is pre-assigned even
        // though the aggregator has no attributed prompts of its own.
        // And for ResourceCollection (issue #45): a non-null collection advertises resources with
        // listChanged; subscribe stays unadvertised because it is never set in Capabilities.
        var sharedTools = new McpServerPrimitiveCollection<McpServerTool>();
        var sharedPrompts = new McpServerPrimitiveCollection<McpServerPrompt>();
        var sharedResources = new McpServerResourceCollection();
        services.AddOptions<McpServerOptions>()
            .Configure(mcpOpts =>
            {
                mcpOpts.ToolCollection = sharedTools;
                mcpOpts.PromptCollection = sharedPrompts;
                mcpOpts.ResourceCollection = sharedResources;
            });

        var builder = services.AddMcpServer();

        // ServerInstructions is the short hand-written orientation (issue #44); the self skill
        // document is never embedded, it stays behind get_service_skill. On stateless HTTP this
        // delegate runs once per request, so the size is logged exactly once per process.
        var instructionsLogged = 0;
        services.AddOptions<McpServerOptions>()
            .Configure<IOptions<AggregatorOptions>, ILoggerFactory>((mcpOpts, aggOpts, loggerFactory) =>
            {
                var agg = aggOpts.Value;
                mcpOpts.ServerInfo = new Implementation
                {
                    Name = agg.SelfName,
                    Title = "MCP Aggregator",
                    Version = version,
                };

                var instructions = AggregatorInstructions.Build(agg.SelfName);
                mcpOpts.ServerInstructions = instructions;

                if (Interlocked.CompareExchange(ref instructionsLogged, 1, 0) == 0)
                {
                    var logger = loggerFactory.CreateLogger(typeof(AggregatorInstructions));
                    var bytes = Encoding.UTF8.GetByteCount(instructions);
                    logger.LogInformation("MCP server instructions: {Chars} chars, {Bytes} UTF-8 bytes",
                        instructions.Length, bytes);
                    if (instructions.Length > AggregatorInstructions.MaxChars)
                        logger.LogWarning("MCP server instructions exceed the {MaxChars}-char ceiling ({Chars} chars); trim AggregatorInstructions",
                            AggregatorInstructions.MaxChars, instructions.Length);
                }
            });

        // Give the aggregator's own tools the same self-correcting argument help that
        // ToolProxyHandler gives proxied downstream calls: without this, a binding failure reaches
        // the caller as a bare "An error occurred invoking 'X'".
        services.AddOptions<McpServerOptions>()
            .Configure<ILoggerFactory>((mcpOpts, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(typeof(AggregatorToolErrorFilter));
                mcpOpts.Filters.Request.CallToolFilters.Add(
                    next => AggregatorToolErrorFilter.Create(next, logger));
            });

        // Typed wrapper tools (issue #39). The catalog adds and removes DownstreamToolWrapper
        // instances in the shared ToolCollection; on a stateful transport the SDK's server watches
        // that collection and sends notifications/tools/list_changed itself, and on stateless HTTP
        // every per-request server lists from it. McpServer is not in DI, so the
        // IOptions<McpServerOptions> singleton is the handle the catalog uses. Registered here
        // rather than in AddAggregatorCore because it depends on the MCP server options.
        services.PostConfigure<McpServerOptions>(mcpOpts =>
        {
            // Something replaced a collection after our Configure ran: fold its entries into the
            // shared one (TryAdd is idempotent by name) and put the shared one back.
            if (!ReferenceEquals(mcpOpts.ToolCollection, sharedTools))
            {
                if (mcpOpts.ToolCollection is { } otherTools)
                {
                    foreach (var tool in otherTools)
                        sharedTools.TryAdd(tool);
                }
                mcpOpts.ToolCollection = sharedTools;
            }

            if (!ReferenceEquals(mcpOpts.PromptCollection, sharedPrompts))
            {
                if (mcpOpts.PromptCollection is { } otherPrompts)
                {
                    foreach (var prompt in otherPrompts)
                        sharedPrompts.TryAdd(prompt);
                }
                mcpOpts.PromptCollection = sharedPrompts;
            }

            if (!ReferenceEquals(mcpOpts.ResourceCollection, sharedResources))
            {
                if (mcpOpts.ResourceCollection is { } otherResources)
                {
                    foreach (var resource in otherResources)
                        sharedResources.TryAdd(resource);
                }
                mcpOpts.ResourceCollection = sharedResources;
            }

            // WithSubscribeToResourcesHandler below advertises resources.subscribe on the author's
            // behalf. The handler exists only to reject subscriptions with a readable message, so
            // the capability must stay unadvertised (issue #45).
            if (mcpOpts.Capabilities?.Resources is { } resourcesCapability)
                resourcesCapability.Subscribe = null;
        });
        services.AddSingleton<WrapperToolCatalog>();
        services.AddHostedService<WrapperSyncHostedService>();

        // Progressive disclosure for the aggregator's own surface too. AdminTools is not scanned by
        // WithToolsFromAssembly; AdminToolSet builds those tools, and only Eager mode puts them in
        // the shared list. In Lazy mode they are disclosed per session (show_admin_tools) or
        // dispatched by name. AdminToolSet depends on nothing but the provider, so consulting it
        // while McpServerOptions are being built creates no cycle with the catalog (which itself
        // depends on IOptions<McpServerOptions>).
        services.AddSingleton<AdminToolSet>();
        services.AddOptions<McpServerOptions>()
            .PostConfigure<AdminToolSet, IOptions<AggregatorOptions>>((mcpOpts, adminTools, aggOpts) =>
            {
                if (aggOpts.Value.WrapperMode != WrapperToolMode.Eager)
                    return;

                foreach (var tool in adminTools.Tools)
                    mcpOpts.ToolCollection!.TryAdd(tool);
            });

        // Lazy mode is per session. The SDK lists the shared collection and then appends whatever
        // this handler returns, so each session sees only the wrappers it activated; and a call to
        // a name the collection does not hold falls through to the second handler, which resolves
        // any wrapper by name so a client that learned a name from find_tools can call it whether
        // or not its host ever re-listed. Neither handler touches the shared collection.
        builder.WithListToolsHandler((request, ct) =>
        {
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();
            var wrappers = catalog?.ActivatedFor(request.Server) ?? [];
            return ValueTask.FromResult(new ListToolsResult
            {
                Tools = wrappers.Select(w => w.ProtocolTool).ToList()
            });
        });

        builder.WithCallToolHandler(async (request, ct) =>
        {
            var name = request.Params?.Name;
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();

            if (name is { Length: > 0 } && catalog is not null)
            {
                if (catalog.TryGetHiddenTool(name, out var hiddenTool))
                {
                    await catalog.ActivateAsync(request.Server, [hiddenTool], ct);
                    return await hiddenTool.InvokeAsync(request, ct);
                }

                DownstreamToolWrapper? wrapper;
                try
                {
                    wrapper = await catalog.ResolveAsync(name, ct);
                }
                catch (AggregatorException ex) when (!ct.IsCancellationRequested)
                {
                    // Registered and enabled, but unreachable: a result the caller can act on.
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }

                if (wrapper is not null)
                {
                    // The session is using it, so list it for that session from now on.
                    await catalog.ActivateAsync(request.Server, [wrapper], ct);
                    return await wrapper.InvokeAsync(request, ct);
                }
            }

            // Same fault the SDK raises; AggregatorToolErrorFilter turns it into a hint.
            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        });

        // Proxied prompts (issue #40) follow the same two-handler shape: the SDK lists the shared
        // PromptCollection and appends this session's activated prompts; prompts/get for a name
        // the collection does not hold resolves any proxied prompt by name and activates it.
        builder.WithListPromptsHandler((request, ct) =>
        {
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();
            var prompts = catalog?.ActivatedPromptsFor(request.Server) ?? [];
            return ValueTask.FromResult(new ListPromptsResult
            {
                Prompts = prompts.Select(p => p.ProtocolPrompt).ToList()
            });
        });

        builder.WithGetPromptHandler(async (request, ct) =>
        {
            var name = request.Params?.Name;
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();

            if (name is { Length: > 0 } && catalog is not null)
            {
                DownstreamPromptWrapper? wrapper;
                try
                {
                    wrapper = await catalog.ResolvePromptAsync(name, ct);
                }
                catch (AggregatorException ex) when (!ct.IsCancellationRequested)
                {
                    // Registered and enabled, but unreachable. Prompts have no error result, so
                    // the readable message travels as the protocol error text.
                    throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
                }

                if (wrapper is not null)
                {
                    await catalog.ActivatePromptsAsync(request.Server, [wrapper], ct);
                    return await wrapper.GetAsync(request, ct);
                }
            }

            throw new McpProtocolException(
                $"Unknown prompt: '{name}'. Downstream prompts are named '{{server}}{WrapperNaming.Separator}{{prompt}}'; " +
                "call find_tools or get_service_details to list them.",
                McpErrorCode.InvalidParams);
        });

        // Bridged resources (issue #45), the same shape again: the SDK lists the shared
        // ResourceCollection and appends this session's activated resources (plain ones in
        // resources/list, templated ones in resources/templates/list); resources/read for a URI the
        // collection does not hold resolves any bridged resource by URI and activates it.
        builder.WithListResourcesHandler((request, ct) =>
        {
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();
            var resources = catalog?.ActivatedResourcesFor(request.Server) ?? [];
            return ValueTask.FromResult(new ListResourcesResult
            {
                Resources = resources.Where(r => !r.IsTemplated).Select(r => r.ProtocolResource).OfType<Resource>().ToList()
            });
        });

        builder.WithListResourceTemplatesHandler((request, ct) =>
        {
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();
            var resources = catalog?.ActivatedResourcesFor(request.Server) ?? [];
            return ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = resources.Where(r => r.IsTemplated).Select(r => r.ProtocolResourceTemplate).ToList()
            });
        });

        builder.WithReadResourceHandler(async (request, ct) =>
        {
            var uri = request.Params?.Uri;
            var catalog = request.Server?.Services?.GetService<WrapperToolCatalog>();

            if (uri is { Length: > 0 } && catalog is not null)
            {
                DownstreamResourceWrapper? wrapper;
                try
                {
                    wrapper = await catalog.ResolveResourceAsync(uri, ct);
                }
                catch (AggregatorException ex) when (!ct.IsCancellationRequested)
                {
                    // Registered and enabled, but unreachable. Resources have no error result, so
                    // the readable message travels as the protocol error text.
                    throw new McpProtocolException(ex.Message, McpErrorCode.InternalError);
                }

                if (wrapper is not null)
                {
                    await catalog.ActivateResourcesAsync(request.Server, [wrapper], ct);
                    return await wrapper.ReadAsync(request, ct);
                }
            }

            // The SDK's own default does the same split: -32002 for clients that negotiated a
            // pre-2026-07-28 revision, InvalidParams for 2026-07-28 and later (its helper is internal).
            var hint = catalog is not null
                ? await catalog.BuildUnknownResourceHintAsync(uri, ct)
                : $"Unknown resource URI: '{uri}'";
            throw new McpProtocolException(hint, UnknownResourceErrorCode(request.Server?.NegotiatedProtocolVersion));
        });

        // Subscriptions are not bridged (the aggregator does not fan out resources/updated), and
        // the capability is not advertised. A client that tries anyway gets a truthful answer
        // instead of the SDK's silent no-op default.
        builder.WithSubscribeToResourcesHandler((request, ct) =>
            throw new McpProtocolException(
                $"Resource subscriptions are not supported by this aggregator (requested '{request.Params?.Uri}'). " +
                "Re-read the resource when you need fresh contents.",
                McpErrorCode.InvalidRequest));

        builder.WithUnsubscribeFromResourcesHandler((request, ct) =>
            throw new McpProtocolException(
                $"Resource subscriptions are not supported by this aggregator (requested '{request.Params?.Uri}'), so there is nothing to unsubscribe.",
                McpErrorCode.InvalidRequest));

        return builder;
    }

    /// <summary>
    /// The 2026-07-28 revision replaced the resource-specific <c>-32002</c> with plain
    /// <c>InvalidParams</c>; earlier clients still expect <c>ResourceNotFound</c>. Ordinal compare on
    /// the ISO date string. A missing version (nothing negotiated yet) is treated as legacy.
    /// </summary>
    internal static McpErrorCode UnknownResourceErrorCode(string? negotiatedProtocolVersion)
        => negotiatedProtocolVersion is { Length: > 0 } version
           && string.CompareOrdinal(version, "2026-07-28") >= 0
            ? McpErrorCode.InvalidParams
            : McpErrorCode.ResourceNotFound;

    private static string GetAggregatorVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(McpServerBuilderExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
