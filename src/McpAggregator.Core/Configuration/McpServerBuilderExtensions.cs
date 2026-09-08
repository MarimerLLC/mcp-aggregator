using System.Reflection;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Configuration;

public static class McpServerBuilderExtensions
{
    // Cap on how much of the self skill document we embed in the MCP initialize-handshake
    // instructions payload. Larger skills are still available through get_service_skill,
    // but smaller payloads keep the handshake cheap for every client.
    private const int MaxEmbeddedSkillChars = 16 * 1024;

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
        var sharedTools = new McpServerPrimitiveCollection<McpServerTool>();
        services.AddOptions<McpServerOptions>()
            .Configure(mcpOpts => mcpOpts.ToolCollection = sharedTools);

        var builder = services.AddMcpServer();

        services.AddOptions<McpServerOptions>()
            .Configure<IOptions<AggregatorOptions>>((mcpOpts, aggOpts) =>
            {
                var agg = aggOpts.Value;
                mcpOpts.ServerInfo = new Implementation
                {
                    Name = agg.SelfName,
                    Title = "MCP Aggregator",
                    Version = version,
                };
                mcpOpts.ServerInstructions = BuildInstructions(agg.SelfName, LoadSelfSkill(agg));
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
            if (ReferenceEquals(mcpOpts.ToolCollection, sharedTools))
                return;

            // Something replaced the collection after our Configure ran: fold its tools into the
            // shared one (TryAdd is idempotent by name) and put the shared one back.
            if (mcpOpts.ToolCollection is { } other)
            {
                foreach (var tool in other)
                    sharedTools.TryAdd(tool);
            }
            mcpOpts.ToolCollection = sharedTools;
        });
        services.AddSingleton<WrapperToolCatalog>();
        services.AddHostedService<WrapperSyncHostedService>();

        return builder;
    }

    private static string? LoadSelfSkill(AggregatorOptions options)
    {
        var path = Path.Combine(options.SkillsDirectoryPath, $"{options.SelfName}.md");
        if (!File.Exists(path))
            return null;

        try
        {
            var content = File.ReadAllText(path);
            return content.Length > MaxEmbeddedSkillChars
                ? content[..MaxEmbeddedSkillChars] + "\n\n[…truncated — call get_service_skill for full text]"
                : content;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildInstructions(string selfName, string? selfSkill)
    {
        var header = $"""
            MCP Aggregator — a single MCP endpoint that fans out to many downstream MCP servers.
            One connection gives the client the union of tools across every registered server,
            without consuming a slot per server in clients that cap concurrent MCP connections.

            Downstream tools are exposed as typed tools named "<server>__<tool>" (for example
            "microsoft-learn__microsoft_docs_search") that take the downstream tool's own parameters.

            Workflow:
              1. find_tools(query: "<what you need>") — search every downstream for matching tools.
                 Results include each tool's exact name and input schema, and make the tools callable.
              2. Call the "<server>__<tool>" tool directly with its listed parameters.
              3. list_services() / get_service_details(serverName) — browse servers and schemas instead
                 of searching; get_service_details also makes that server's typed tools callable.
              4. get_service_skill(serverName: "{selfName}") — full usage guide for this aggregator;
                 get_service_skill(serverName: "<downstream>") — usage guide for a specific service.
              5. invoke_tool(serverName, toolName, arguments) — escape hatch only, when a typed tool is
                 not (yet) in your tool list. arguments is a JSON object encoded as a string.

            Downstream connections are established lazily on first use and reused across calls.
            Start by calling find_tools or get_service_skill(serverName: "{selfName}").
            """;

        if (string.IsNullOrWhiteSpace(selfSkill))
            return header;

        return header + "\n\n---\n\n# Aggregator Skill Document\n\n" + selfSkill;
    }

    private static string GetAggregatorVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(McpServerBuilderExtensions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
